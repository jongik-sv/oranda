using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Tuning.Cursors;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

// 언어에 맞는 서식 글: 서식이 언어마다 달라 CompositeFormat 으로 미리 만들어 둘 수 없다.
#pragma warning disable CA1863

namespace Oranda.App.Features.Tuning.Report;

/// <summary>자식 커서 고르기 한 칸(null = 전체).</summary>
public sealed record ReportChildChoice(int? ChildNumber, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 「튜닝 보고서 만들기」 대화 상자(시안 화면 k). 대상 sql_id·자식 커서·담을 절·바인드 값(기본 끔)·저장 위치를 고르고
/// <see cref="TuningReportCollector"/>(SELECT 만) → <see cref="TuningReportHtml"/> → 파일 쓰기를 한다. 만드는 중에는 같은 창에서 진행·취소.
/// 팩 절(AWR)은 보유 선언이 없으면 꺼진 채 이유를 보인다. 운영 접속이면 안내 한 줄을 보인다.
/// </summary>
public sealed partial class TuningReportDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>마지막으로 저장한 폴더(앱이 켜져 있는 동안 기억).</summary>
    private static string? _lastFolder;

    private readonly ISession? _session;
    private readonly ITuningService? _tuning;
    private readonly IMonitorService? _monitor;
    private CancellationTokenSource? _cts;
    private bool _pathTouched;

    [ObservableProperty]
    private string _sqlId = string.Empty;

    [ObservableProperty]
    private ReportChildChoice? _selectedChild;

    [ObservableProperty]
    private bool _includeSummary = true;

    [ObservableProperty]
    private bool _includeSqlText = true;

    [ObservableProperty]
    private bool _includeBindValues;

    [ObservableProperty]
    private bool _includePlans = true;

    [ObservableProperty]
    private bool _includeGraph = true;

    [ObservableProperty]
    private bool _includeExecStats = true;

    [ObservableProperty]
    private bool _includeObjectStats = true;

    [ObservableProperty]
    private bool _includeChildCursors = true;

    [ObservableProperty]
    private bool _includeWaits = true;

    [ObservableProperty]
    private bool _includeAwr;

    [ObservableProperty]
    private bool _includeCandidates = true;

    [ObservableProperty]
    private bool _includeEnvironment = true;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _openAfter = true;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isLoadingChildren;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _progressCount = string.Empty;

    [ObservableProperty]
    private string _finishedText = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _childNotice;

    /// <summary>시험·디자이너용(접속 없음 화면).</summary>
    public TuningReportDialogViewModel()
        : this(null, null, null)
    {
    }

    public TuningReportDialogViewModel(ISession? session, ITuningService? tuning, IMonitorService? monitor, string? sqlId = null, string? editorText = null)
    {
        _session = session;
        _tuning = tuning;
        _monitor = monitor;
        EditorText = string.IsNullOrWhiteSpace(editorText) ? null : editorText;
        Held = session is null ? LicensePack.None : TuningReportCollector.HeldPack(session);
        IncludeAwr = Held >= LicensePack.Diagnostics;
        ChildChoices.Add(new ReportChildChoice(null, L("전체", "All")));
        _selectedChild = ChildChoices[0];
        _sqlId = sqlId?.Trim() ?? string.Empty;
        _filePath = DefaultPath(_sqlId, DateTimeOffset.Now);
    }

    public ObservableCollection<ReportChildChoice> ChildChoices { get; } = [];

    /// <summary>공유 풀에 글이 없을 때 쓸 편집기 문장.</summary>
    public string? EditorText { get; }

    /// <summary>대상 설명(어디서 왔는지). 비면 숨긴다.</summary>
    public string SourceText { get; init; } = string.Empty;

    public bool HasSourceText => SourceText.Length > 0;

    public LicensePack Held { get; }

    /// <summary>파일을 쓰는 방법(시험은 메모리로 바꾼다).</summary>
    public Func<string, string, CancellationToken, Task> WriteFile { get; set; } = async (path, html, ct) =>
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, html, new UTF8Encoding(false), ct).ConfigureAwait(false);
    };

    /// <summary>수집기 설정(대기 표본 쉬기 등). 시험이 바꾼다.</summary>
    public Func<ITuningService, IMonitorService?, TuningReportCollector> CreateCollector { get; set; } =
        (t, m) => new TuningReportCollector(t, m) { AppVersion = Update.AppVersion.Assembly };

    /// <summary>대기 표본 초(팩 없을 때).</summary>
    public int WaitSampleSeconds { get; set; } = 30;

    /// <summary>마지막으로 만든 파일 경로(성공했을 때).</summary>
    public string? ResultPath { get; private set; }

    /// <summary>만들기가 끝나 창을 닫아야 할 때.</summary>
    public event EventHandler? Completed;

    // ---- 화면 글 ----

    public bool HasSession => _session is not null && _tuning is not null;

    public bool ShowNoSession => !HasSession;

    public string ConnectionText => _session is { } s ? (s.Server.SessionUser ?? s.Profile.User) + "@" + s.Profile.Name : L("접속 없음", "No connection");

    public string ConnectionDetail => _session is { Purpose: SessionPurpose.Monitor } ? L("· 모니터 전용 세션", "· monitor session") : string.Empty;

    public ConnectionEnvironment Environment => _session?.Profile.Environment ?? ConnectionEnvironment.Development;

    public string EnvironmentLabel => Environment switch
    {
        ConnectionEnvironment.Production => L("운영", "Production"),
        ConnectionEnvironment.Test => L("테스트", "Test"),
        _ => L("개발", "Development"),
    };

    /// <summary>환경 칩 바탕 자원 키.</summary>
    public string EnvironmentBrushKey => Environment switch
    {
        ConnectionEnvironment.Production => "OrandaEnvProductionBrush",
        ConnectionEnvironment.Test => "OrandaEnvTestBrush",
        _ => "OrandaEnvDevelopmentBrush",
    };

    public bool IsProduction => _session is not null && Environment == ConnectionEnvironment.Production;

    public static string ProductionNotice => L(
        "운영 접속입니다. 조회는 SELECT 뿐이지만 gv$ 뷰·딕셔너리를 여러 번 읽습니다. 바쁜 시간에는 대기 분해를 꺼 두세요.",
        "This is a production connection. Queries are SELECT only but read gv$ views and the dictionary several times. Turn off the wait breakdown at busy hours.");

    public static string BindWarning => L("고객·업무 값이 들어갈 수 있습니다. 기본 끔", "May contain customer or business values. Off by default");

    public string WaitNote => Held >= LicensePack.Diagnostics
        ? L("ASH 에서 지난 1 시간을 읽습니다(Diagnostics Pack)", "Reads the last hour from ASH (Diagnostics Pack)")
        : string.Format(CultureInfo.InvariantCulture, L("지금 {0} 초 표본을 모읍니다(만들기가 {0} 초 늘어남)", "Samples for {0} s now (adds {0} s)"), WaitSampleSeconds);

    public bool AwrAvailable => HasSession && Held >= LicensePack.Diagnostics;

    public string AwrNote => AwrAvailable
        ? L("dba_hist_* 를 읽습니다(Diagnostics Pack 선언됨)", "Reads dba_hist_* (Diagnostics Pack declared)")
        : L("Diagnostics Pack 선언 없음 — 이유만 적힘", "No Diagnostics Pack declared — only the reason is written");

    public static string ReadOnlyNote => L("모든 자료는 SELECT 로만 모읍니다. DB 에 쓰지 않습니다. 파일에는 스크립트·외부 자원이 없습니다.",
        "Everything is read with SELECT only. Nothing is written to the database. The file has no scripts or external resources.");

    /// <summary>만들기를 못 하는 이유. 할 수 있으면 null.</summary>
    public string? CreateBlockedReason
    {
        get
        {
            if (!HasSession)
            {
                return L("접속이 없습니다. 접속한 뒤 다시 여세요.", "No connection. Connect and open this again.");
            }

            if (!ChildCursorsViewModel.IsSqlId(SqlId.Trim()))
            {
                return L("sql_id 가 아닙니다(13 글자, 영문 소문자·숫자).", "Not a sql_id (13 lowercase letters and digits).");
            }

            if (Sections().Count == 0)
            {
                return L("담을 절을 하나 이상 고르세요.", "Choose at least one section.");
            }

            if (string.IsNullOrWhiteSpace(FilePath) || !Path.IsPathFullyQualified(FilePath.Trim()))
            {
                return L("저장 위치를 고르세요(전체 경로).", "Choose where to save (full path).");
            }

            return null;
        }
    }

    public bool CanCreate => !IsRunning && CreateBlockedReason is null;

    public bool ShowForm => !IsRunning;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasChildNotice => !string.IsNullOrEmpty(ChildNotice);

    /// <summary>단추 줄 왼쪽 예상(질의 수·시간).</summary>
    public string EstimateText
    {
        get
        {
            if (CreateBlockedReason is { } why)
            {
                return why;
            }

            var children = Math.Min(Math.Max(ChildChoices.Count - 1, 1), TuningReportCollector.MaxPlans);
            if (SelectedChild?.ChildNumber is not null)
            {
                children = 1;
            }

            var queries = 0;
            var plans = IncludePlans || IncludeGraph || IncludeCandidates || IncludeObjectStats || IncludeSummary;
            queries += IncludeSqlText || IncludeCandidates || IncludeSummary ? 2 : 0;
            queries += plans || IncludeChildCursors ? 2 : 0;
            queries += plans ? children : 0;
            queries += IncludeExecStats || IncludeSummary ? 1 : 0;
            queries += IncludeObjectStats || IncludeSummary || IncludeCandidates ? 3 * 4 : 0; // 표 3개 어림, 표마다 4 질의
            queries += IncludeEnvironment ? 1 : 0;
            queries += IncludeAwr && AwrAvailable ? 1 : 0;
            var seconds = 3 + queries / 4;
            if (IncludeWaits)
            {
                if (Held >= LicensePack.Diagnostics)
                {
                    queries += 1;
                }
                else
                {
                    queries += WaitSampleSeconds;
                    seconds += WaitSampleSeconds;
                }
            }

            return string.Format(CultureInfo.InvariantCulture, L("예상 {0} 초 · 질의 약 {1}개", "About {0} s · about {1} queries"), seconds, queries);
        }
    }

    public bool EstimateIsWarning => CreateBlockedReason is not null && HasSession;

    // ---- 동작 ----

    /// <summary>고른 절.</summary>
    public HashSet<TuningReportSection> Sections()
    {
        var set = new HashSet<TuningReportSection>();
        void Add(bool on, TuningReportSection s)
        {
            if (on)
            {
                set.Add(s);
            }
        }

        Add(IncludeSummary, TuningReportSection.Summary);
        Add(IncludeSqlText, TuningReportSection.SqlText);
        Add(IncludePlans, TuningReportSection.Plans);
        Add(IncludeGraph, TuningReportSection.Graph);
        Add(IncludeExecStats, TuningReportSection.ExecStats);
        Add(IncludeObjectStats, TuningReportSection.ObjectStats);
        Add(IncludeChildCursors, TuningReportSection.ChildCursors);
        Add(IncludeWaits, TuningReportSection.Waits);
        Add(IncludeAwr, TuningReportSection.Awr); // 팩 없으면 보고서에 「포함 안 함: 이유」 로 남는다(서버로 보내지 않는다)
        Add(IncludeCandidates, TuningReportSection.Candidates);
        Add(IncludeEnvironment, TuningReportSection.Environment);
        return set;
    }

    /// <summary>만들 때 쓰는 선택. 팩이 없으면 AWR 절은 늘 담아 이유를 적는다.</summary>
    public TuningReportOptions BuildOptions()
    {
        var sections = Sections();
        if (!AwrAvailable)
        {
            sections.Add(TuningReportSection.Awr);
        }

        return new TuningReportOptions
        {
            SqlId = SqlId.Trim(),
            Sections = sections,
            IncludeBindValues = IncludeBindValues,
            ChildNumber = SelectedChild?.ChildNumber,
            WaitSampleSeconds = WaitSampleSeconds,
            English = LiteralTranslator.IsEnglish,
            EditorText = EditorText,
        };
    }

    /// <summary>자식 커서 목록을 읽어 고르기 칸을 채운다(SELECT 한 번). 없으면 안내 한 줄.</summary>
    public async Task LoadChildrenAsync(CancellationToken ct = default)
    {
        if (!HasSession || !ChildCursorsViewModel.IsSqlId(SqlId.Trim()))
        {
            return;
        }

        IsLoadingChildren = true;
        try
        {
            var children = await _tuning!.GetChildCursorsAsync(_session!, SqlId.Trim(), ct).ConfigureAwait(true);
            var keep = SelectedChild?.ChildNumber;
            ChildChoices.Clear();
            var hashes = children.Select(c => c.PlanHashValue).Distinct().Count();
            ChildChoices.Add(new ReportChildChoice(null, children.Count == 0
                ? L("전체", "All")
                : string.Format(CultureInfo.InvariantCulture, L("전체 ({0}개 · plan_hash {1})", "All ({0} · {1} plan hashes)"), children.Count, hashes)));
            foreach (var g in children.GroupBy(c => c.ChildNumber).OrderBy(g => g.Key))
            {
                var c = g.OrderByDescending(x => x.Executions).First();
                ChildChoices.Add(new ReportChildChoice(c.ChildNumber, string.Format(CultureInfo.InvariantCulture,
                    L("child {0} · plan_hash {1} · 실행 {2:N0}", "child {0} · plan_hash {1} · {2:N0} execs"), c.ChildNumber, c.PlanHashValue, g.Sum(x => x.Executions))));
            }

            SelectedChild = ChildChoices.FirstOrDefault(c => c.ChildNumber == keep) ?? ChildChoices[0];
            ChildNotice = children.Count == 0
                ? L("공유 풀에 이 sql_id 의 커서가 없습니다 — 계획·통계 절은 「수집 실패」 로 남습니다. 문장을 한 번 실행한 뒤 만드세요.",
                    "The shared pool has no cursor for this sql_id — plan and statistics sections will say \"failed\". Run the statement once first.")
                : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ChildNotice = L("자식 커서를 읽지 못했습니다: ", "Could not read child cursors: ") + FirstLine(ex.Message);
        }
        finally
        {
            IsLoadingChildren = false;
            Raise();
        }
    }

    /// <summary>모으고 → HTML 을 만들고 → 파일에 쓴다. 성공하면 경로(그리고 <see cref="Completed"/>), 취소·실패면 null.</summary>
    public async Task<string?> CreateAsync()
    {
        if (!CanCreate)
        {
            return null;
        }

        var path = FilePath.Trim();
        var options = BuildOptions();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        ErrorText = null;
        ProgressValue = 0;
        ProgressText = L("시작하는 중…", "Starting…");
        FinishedText = string.Empty;
        Raise();
        try
        {
            var collector = CreateCollector(_tuning!, _monitor);
            var progress = new SyncProgress(OnProgress);
            var data = await collector.CollectAsync(_session!, options, progress, ct).ConfigureAwait(true);
            var html = TuningReportHtml.Build(data);
            ct.ThrowIfCancellationRequested();
            await WriteFile(path, html, ct).ConfigureAwait(true);
            _lastFolder = Path.GetDirectoryName(path);
            ResultPath = path;
            Completed?.Invoke(this, EventArgs.Empty);
            return path;
        }
        catch (OperationCanceledException)
        {
            ErrorText = L("만들기를 취소했습니다. 파일은 만들지 않았습니다.", "Cancelled. No file was written.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            ErrorText = L("파일을 쓰지 못했습니다: ", "Could not write the file: ") + FirstLine(ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorText = L("보고서를 만들지 못했습니다: ", "Could not make the report: ") + FirstLine(ex.Message);
            return null;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            Raise();
        }
    }

    /// <summary>만드는 중이면 멈춘다.</summary>
    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>찾아보기에서 고른 경로.</summary>
    public void SetPickedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FilePath = path;
        _pathTouched = true;
    }

    public string SuggestedFileName => TuningReportHtml.SuggestedFileName(SqlId, DateTimeOffset.Now);

    private void OnProgress(TuningReportProgress p)
    {
        ProgressValue = p.Fraction * 100;
        ProgressText = p.Current;
        ProgressCount = string.Format(CultureInfo.InvariantCulture, L("절 {0} / {1}", "Section {0} / {1}"), p.Done, p.Total);
        FinishedText = p.Finished.Count == 0 ? string.Empty : L("끝난 절: ", "Done: ") + string.Join(" · ", p.Finished);
    }

    internal static string DefaultPath(string sqlId, DateTimeOffset at)
    {
        var folder = _lastFolder;
        if (string.IsNullOrEmpty(folder))
        {
            var docs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            folder = Path.Combine(string.IsNullOrEmpty(docs) ? Path.GetTempPath() : docs, "Oranda");
        }

        return Path.Combine(folder, TuningReportHtml.SuggestedFileName(sqlId, at));
    }

    partial void OnSqlIdChanged(string value)
    {
        if (!_pathTouched)
        {
            FilePath = DefaultPath(value, DateTimeOffset.Now);
        }

        Raise();
    }

    partial void OnIsRunningChanged(bool value) => Raise();

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnChildNoticeChanged(string? value) => OnPropertyChanged(nameof(HasChildNotice));

    partial void OnSelectedChildChanged(ReportChildChoice? value) => Raise();

    partial void OnFilePathChanged(string value) => Raise();

    partial void OnIncludeSummaryChanged(bool value) => Raise();

    partial void OnIncludeSqlTextChanged(bool value) => Raise();

    partial void OnIncludePlansChanged(bool value) => Raise();

    partial void OnIncludeGraphChanged(bool value) => Raise();

    partial void OnIncludeExecStatsChanged(bool value) => Raise();

    partial void OnIncludeObjectStatsChanged(bool value) => Raise();

    partial void OnIncludeChildCursorsChanged(bool value) => Raise();

    partial void OnIncludeWaitsChanged(bool value) => Raise();

    partial void OnIncludeAwrChanged(bool value) => Raise();

    partial void OnIncludeCandidatesChanged(bool value) => Raise();

    partial void OnIncludeEnvironmentChanged(bool value) => Raise();

    private void Raise()
    {
        foreach (var name in new[] { nameof(CreateBlockedReason), nameof(CanCreate), nameof(ShowForm), nameof(EstimateText), nameof(EstimateIsWarning) })
        {
            OnPropertyChanged(name);
        }
    }

    private static string FirstLine(string message) =>
        message.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private static string L(string ko, string en) => LiteralTranslator.IsEnglish ? en : ko;

    /// <summary>보고를 바로 부른다(수집기는 UI 스레드에서 이어 돈다 — ConfigureAwait(true)).</summary>
    private sealed class SyncProgress(Action<TuningReportProgress> action) : IProgress<TuningReportProgress>
    {
        public void Report(TuningReportProgress value) => action(value);
    }
}
