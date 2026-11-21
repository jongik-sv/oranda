using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>히스토그램 고르기 한 칸(이름표 + 값).</summary>
public sealed record GatherStatsHistogramChoice(GatherStatsHistogram Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>확인 창의 대상 테이블 한 줄(상태·걸린 시간·결과 글).</summary>
public sealed partial class GatherStatsRow(TableStatsTarget target) : ObservableObject
{
    public TableStatsTarget Target { get; } = target;

    public string Display => Target.Display;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private GatherStatsOutcome _outcome = GatherStatsOutcome.Waiting;

    [ObservableProperty]
    private string _elapsedText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    public string StatusText => Outcome switch
    {
        GatherStatsOutcome.Running => T("수집 중", "Running"),
        GatherStatsOutcome.Succeeded => T("성공", "Done"),
        GatherStatsOutcome.Failed => T("실패", "Failed"),
        GatherStatsOutcome.Cancelled => T("취소", "Cancelled"),
        _ => T("대기", "Waiting"),
    };

    internal void Apply(GatherStatsResult r)
    {
        Outcome = r.Outcome;
        ElapsedText = r.Outcome is GatherStatsOutcome.Running or GatherStatsOutcome.Waiting || r.Elapsed == TimeSpan.Zero ? string.Empty : FormatElapsed(r.Elapsed);
        Message = r.Outcome == GatherStatsOutcome.Succeeded ? string.Empty : r.Message;
    }

    internal static string FormatElapsed(TimeSpan t) => t.TotalSeconds < 60
        ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.0}s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m {t.Seconds:00}s");
}

/// <summary>
/// 「통계 수집…」 확인 창(SG-1): 대상 테이블, 옵션(표본·히스토그램·인덱스 포함·병렬), 실행할 PL/SQL 미리 보기, 「스크립트로 열기」.
/// 운영 접속이면 경고를 크게, 읽기 전용 접속이면 실행을 막는다. 실행은 전용 세션(<see cref="OpenSession"/>)에서 테이블마다 차례로 하고
/// 창은 그대로 남아 진행·결과를 보인다. 실행 중 닫기·「취소」 = 세션 cancel.
/// </summary>
public sealed partial class GatherStatsDialogViewModel : ObservableObject, IDisposable
{
    private readonly GatherStatsRunner _runner;
    private readonly Func<CancellationToken, Task<ISession?>> _openSession;
    private readonly GatherStatsDialect _dialect;
    private CancellationTokenSource? _cts;

    /// <param name="profile">문장을 보낼 접속(머리 띠·운영 경고·공급자).</param>
    /// <param name="openSession">실행용 세션을 연다(셸 = 같은 접속의 전용 세션). 다 쓰면 이 창이 닫는다. null 을 주면 실행하지 않는다.</param>
    public GatherStatsDialogViewModel(ConnectionProfile profile, IReadOnlyList<TableStatsTarget> targets, GatherStatsRunner runner,
        Func<CancellationToken, Task<ISession?>> openSession)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
        _dialect = GatherStatsDialect.For(profile.Dbms);
        Rows = new ObservableCollection<GatherStatsRow>(targets.Select(t => new GatherStatsRow(t)));
        HistogramChoices =
        [
            new(GatherStatsHistogram.Auto, T("자동 (SIZE AUTO)", "Auto (SIZE AUTO)")),
            new(GatherStatsHistogram.None, T("히스토그램 없음 (SIZE 1)", "No histograms (SIZE 1)")),
            .. _dialect.Support.SkewOnly ? [new GatherStatsHistogramChoice(GatherStatsHistogram.SkewOnly, T("치우친 열만 (SIZE SKEWONLY)", "Skewed columns only (SIZE SKEWONLY)"))] : Array.Empty<GatherStatsHistogramChoice>(),
        ];
        _selectedHistogram = HistogramChoices[0];
    }

    /// <summary>실행이 끝나면 성공한 테이블을 알린다(열 통계 캐시·열린 통계 보기 새로 읽기).</summary>
    public event EventHandler<IReadOnlyList<TableStatsTarget>>? Completed;

    public ConnectionProfile Profile { get; }

    public ObservableCollection<GatherStatsRow> Rows { get; }

    public IReadOnlyList<GatherStatsHistogramChoice> HistogramChoices { get; }

    public GatherStatsSupport Support => _dialect.Support;

    public static string Title => T("통계 수집", "Gather statistics");

    public string Question => Rows.Count == 1
        ? T($"{Rows[0].Display} 의 통계를 수집합니다.", $"Gather statistics on {Rows[0].Display}.")
        : T($"테이블 {Rows.Count}개의 통계를 차례로 수집합니다.", $"Gather statistics on {Rows.Count} tables, one after another.");

    public static string Note => T("딕셔너리 통계를 새로 씁니다. 이 테이블을 쓰는 SQL 의 실행 계획이 바뀔 수 있고, 큰 테이블은 오래 걸립니다. 편집기 세션과 따로 연 세션에서 실행합니다.",
        "This rewrites dictionary statistics. Plans of SQL using these tables may change, and large tables take a while. It runs on a separate session, not the editor's.");

    public bool IsProduction => Profile.Environment == ConnectionEnvironment.Production;

    /// <summary>운영 접속 경고(크게 보인다). 운영이 아니면 빈 글.</summary>
    public string ProductionWarning => IsProduction
        ? T($"운영 접속 '{Profile.Name}' 입니다. 업무 시간의 통계 수집은 실행 계획을 바꿔 서비스에 영향을 줄 수 있습니다.",
            $"'{Profile.Name}' is a PRODUCTION connection. Gathering statistics during business hours can change plans and affect the service.")
        : string.Empty;

    public bool HasProductionWarning => IsProduction;

    public string HeaderTitle => Profile.Name;

    public string HeaderDetail => $"{Profile.User}@{Profile.DataSource}";

    public string EnvironmentText => Profile.Environment switch
    {
        ConnectionEnvironment.Production => T("운영", "Production"),
        ConnectionEnvironment.Test => T("테스트", "Test"),
        _ => T("개발", "Development"),
    };

    /// <summary>실행을 막는 까닭(읽기 전용 접속). 없으면 빈 글.</summary>
    public string BlockedText => Profile.ReadOnly
        ? T("읽기 전용 접속이라 실행할 수 없습니다. 「스크립트로 열기」로 문안만 볼 수 있습니다.", "This is a read-only connection, so it cannot run. Use Open as script to see the text.")
        : string.Empty;

    public bool HasBlockedText => BlockedText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ScriptText), nameof(OptionsError), nameof(CanRun), nameof(Options), nameof(HasOptionsError))]
    private bool _autoSample = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ScriptText), nameof(OptionsError), nameof(CanRun), nameof(Options), nameof(HasOptionsError))]
    private string _samplePercentText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ScriptText), nameof(Histogram), nameof(Options))]
    private GatherStatsHistogramChoice _selectedHistogram;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ScriptText), nameof(Options))]
    private bool _cascade = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(ScriptText), nameof(OptionsError), nameof(CanRun), nameof(Options), nameof(HasOptionsError))]
    private string _degreeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanEditOptions), nameof(CancelText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelText))]
    private bool _isDone;

    /// <summary>창 아래 한 줄(진행·요약·세션 오류).</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    public GatherStatsHistogram Histogram
    {
        get => SelectedHistogram.Value;
        set => SelectedHistogram = HistogramChoices.FirstOrDefault(c => c.Value == value) ?? HistogramChoices[0];
    }

    public bool CanEditOptions => !IsRunning;

    public bool CanRun => !IsRunning && !Profile.ReadOnly && Options is not null && Rows.Count > 0;

    /// <summary>오른쪽 단추 글: 실행 중 = 「실행 취소」, 끝 = 「닫기」, 그 전 = 「취소」.</summary>
    public string CancelText => IsRunning ? T("수집 멈추기", "Stop") : IsDone ? T("닫기", "Close") : T("취소", "Cancel");

    /// <summary>입력이 맞으면 옵션, 틀리면 null(<see cref="OptionsError"/>).</summary>
    public GatherStatsOptions? Options => ParseOptions(out _);

    public string OptionsError
    {
        get
        {
            ParseOptions(out var error);
            return error;
        }
    }

    public bool HasOptionsError => OptionsError.Length > 0;

    /// <summary>실행할 PL/SQL(테이블마다 블록 하나). 옵션이 틀리면 안내 한 줄.</summary>
    public string Preview => Options is { } o ? string.Join("\n\n", Rows.Select(r => BuildOrError(r.Target, o))) : "-- " + OptionsError;

    /// <summary>「스크립트로 열기」 본문: 머리 주석 + 블록마다 <c>/</c>.</summary>
    public string ScriptText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("-- ").Append(T("통계 수집 스크립트(Oranda 가 만든 문안, 실행하지 않았습니다). DB 에 씁니다.", "Statistics gathering script (generated by Oranda, not run). It writes to the database."))
                .Append('\n').Append("-- ").Append(T("접속: ", "Connection: ")).Append(Profile.Name).Append(" (").Append(EnvironmentText).Append(")\n");
            var o = Options ?? GatherStatsOptions.Default;
            foreach (var row in Rows)
            {
                sb.Append(BuildOrError(row.Target, o)).Append("\n/\n");
            }

            return sb.ToString();
        }
    }

    /// <summary>편집기 탭 제목: 하나면 <c>gather_EMP.sql</c>, 여럿이면 <c>gather_3_tables.sql</c>.</summary>
    public string ScriptTitle => Rows.Count == 1 ? GatherStatsScript.Title(Rows[0].Target) : $"gather_{Rows.Count}_tables.sql";

    /// <summary>전용 세션을 열고 테이블마다 차례로 수집한다. 끝나면 세션을 닫고 <see cref="Completed"/>.</summary>
    public async Task RunAsync()
    {
        if (!CanRun || Options is not { } options)
        {
            return;
        }

        IsRunning = true;
        IsDone = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        foreach (var row in Rows)
        {
            row.Apply(new GatherStatsResult(row.Target, GatherStatsOutcome.Waiting, TimeSpan.Zero, string.Empty));
        }

        SummaryText = T("세션을 여는 중…", "Opening a session…");
        ISession? session;
        try
        {
            session = await _openSession(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            session = null;
            SummaryText = T("취소했습니다.", "Cancelled.");
            IsRunning = false;
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SummaryText = T("세션을 열지 못했습니다: ", "Could not open a session: ") + FirstLine(ex.Message);
            IsRunning = false;
            return;
        }

        if (session is null)
        {
            SummaryText = T("세션을 열지 않아 실행하지 않았습니다.", "No session was opened, so nothing ran.");
            IsRunning = false;
            return;
        }

        IReadOnlyList<GatherStatsResult> results;
        try
        {
            results = await _runner.RunAsync(session, [.. Rows.Select(r => r.Target)], options, (i, r) =>
            {
                Rows[i].Apply(r);
                if (r.Outcome == GatherStatsOutcome.Running)
                {
                    SummaryText = T($"{i + 1}/{Rows.Count} 수집 중: {r.Target.Display}", $"{i + 1}/{Rows.Count} gathering: {r.Target.Display}");
                }
            }, ct).ConfigureAwait(true);
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning("통계 수집 세션 닫기 실패: {0}", ex.Message);
            }

            IsRunning = false;
        }

        IsDone = true;
        var ok = results.Where(r => r.Outcome == GatherStatsOutcome.Succeeded).Select(r => r.Target).ToList();
        var failed = results.Count(r => r.Outcome == GatherStatsOutcome.Failed);
        var cancelled = results.Count(r => r.Outcome == GatherStatsOutcome.Cancelled);
        SummaryText = T($"성공 {ok.Count} · 실패 {failed} · 취소 {cancelled}", $"Done {ok.Count} · failed {failed} · cancelled {cancelled}");
        if (ok.Count > 0)
        {
            Completed?.Invoke(this, ok);
        }
    }

    /// <summary>실행 중이면 멈춘다(지금 테이블은 드라이버 cancel, 나머지는 「취소」).</summary>
    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private string BuildOrError(TableStatsTarget target, GatherStatsOptions options)
    {
        try
        {
            return _dialect.Build(target.Owner, target.Table, options);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return $"-- {target.Display}: {ex.Message}";
        }
    }

    private GatherStatsOptions? ParseOptions(out string error)
    {
        error = string.Empty;
        decimal? percent = null;
        if (!AutoSample)
        {
            if (!decimal.TryParse(SamplePercentText?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) || p <= 0 || p > 100)
            {
                error = T("표본 비율은 0 보다 크고 100 이하인 수로 넣으세요.", "Enter a sample percent above 0 and up to 100.");
                return null;
            }

            percent = p;
        }

        int? degree = null;
        if (!string.IsNullOrWhiteSpace(DegreeText))
        {
            if (!int.TryParse(DegreeText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) || d < 1 || d > 1024)
            {
                error = T("병렬 정도는 비우거나 1~1024 의 정수로 넣으세요.", "Leave the degree empty or enter a whole number from 1 to 1024.");
                return null;
            }

            degree = d;
        }

        return new GatherStatsOptions { SamplePercent = percent, Histogram = Histogram, Cascade = Cascade, Degree = degree };
    }
}
