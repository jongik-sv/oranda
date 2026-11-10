using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>실행 단계 표의 한 줄: 테이블마다 진행률, 끝난 뒤에는 원본·대상 건수 비교.</summary>
public sealed partial class TableRunRowViewModel(string table) : ViewModelBase
{
    [ObservableProperty]
    private long _rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(HasEstimate))]
    private long? _estimated;

    [ObservableProperty]
    private string _phase = "대기";

    [ObservableProperty]
    private long? _sourceRows;

    [ObservableProperty]
    private long? _targetRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(HasEstimate))]
    private CopyOutcome? _outcome;

    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>대상에 넣은 행 수(적재 중). 읽기·쓰기를 겹치면 읽은 수(<see cref="Rows"/>)보다 늦다. 모르면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    private long? _written;

    /// <summary>
    /// 이 테이블의 적재가 끝났다는 알림(<see cref="CopyRunner.PhaseLoadEnd"/>)을 받았다. 그 뒤 시퀀스·다시 켜기·건수 비교 같은 보고(읽은 수 0)가 와도
    /// 읽은 수·넣은 수·예상 건수는 그대로 두고 단계 글만 바꾼다.
    /// </summary>
    public bool LoadEnded { get; private set; }

    /// <summary>
    /// 적재 끝 알림을 반영한다: 예상 건수(점검 때 센 값) 대신 실제로 읽은 수가 기준이 되어 막대가 이 시점에 찬다.
    /// 실패한 행이 있으면 넣은 수가 읽은 수보다 적어 100% 미만으로 남고, 결과가 오면(<see cref="Percent"/>) 가득 찬다.
    /// </summary>
    internal void EndLoad(long rowsRead, long? written)
    {
        LoadEnded = true;
        Rows = rowsRead;
        Estimated = rowsRead;
        if (written is { } w)
        {
            Written = w;
        }

        Phase = RunPhases.LoadEnded;
    }

    /// <summary>멈춤 표시(결정 70): 「배치 n 쓰는 중 · m분째 대기」 등. 없으면 빈 글자. 단계 칸에 덧붙여 보인다.</summary>
    [ObservableProperty]
    private string _waitText = string.Empty;

    /// <summary>끝난 표의 시간 나눔(결정 74: 「읽기 x초 · 변환 y초 · 쓰기 z초 · n행/초」). 없으면 빈 글자.</summary>
    [ObservableProperty]
    private string _timesText = string.Empty;

    /// <summary>규칙 키(진행·결과가 이 이름으로 온다).</summary>
    public string Table { get; } = table;

    /// <summary>그리드에 보이는 표 이름 <c>계정.표</c>. 정하지 않으면 키 그대로.</summary>
    public string Display { get; init; } = table;

    /// <summary>끝까지 읽은 테이블의 결과(성공·일부 실패)가 정해졌다. 막대는 예상 건수와 상관없이 가득 찬다.</summary>
    private bool ReadCompleted => Outcome is CopyOutcome.Succeeded or CopyOutcome.PartiallyFailed;

    public bool HasEstimate => ReadCompleted || Estimated is > 0;

    /// <summary>
    /// 0~100. 대상에 넣은 행(<see cref="Written"/>, 모르면 읽은 수)을 예상 건수로 나눈다. 끝까지 읽은 테이블은 결과가 오면 100(예상 건수가 어긋나도),
    /// 적재가 끝나면 예상 건수 자리에 실제로 읽은 수가 들어간다(<see cref="EndLoad"/>). 예상 건수가 없으면 0.
    /// </summary>
    public double Percent => ReadCompleted ? 100d
        : Estimated is > 0 ? Math.Min(100d, (Written ?? Rows) * 100d / Estimated.Value) : 0d;

    /// <summary>끝난 뒤 원본·대상 건수가 같은지. 아직 비교 전이면 null.</summary>
    public bool? CountsMatch => SourceRows is { } s && TargetRows is { } t ? s == t : null;

    public string CountsText => SourceRows is { } s && TargetRows is { } t
        ? string.Create(CultureInfo.InvariantCulture, $"{s:N0} → {t:N0}")
        : string.Empty;

    partial void OnRowsChanged(long value) => OnPropertyChanged(nameof(Percent));

    partial void OnSourceRowsChanged(long? value) => NotifyCounts();

    partial void OnTargetRowsChanged(long? value) => NotifyCounts();

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CountsMatch));
        OnPropertyChanged(nameof(CountsText));
    }
}

/// <summary>실패 행 목록의 한 줄(키 열 값과 오류 메시지만).</summary>
public sealed class FailedRowViewModel(FailedRow row, string? display = null)
{
    public FailedRow Row { get; } = row;

    /// <summary>규칙 키.</summary>
    public string Table => Row.Table;

    /// <summary>그리드에 보이는 표 이름 <c>계정.표</c>.</summary>
    public string Display { get; } = display ?? row.Table;

    public string KeyText => string.Join(", ", Row.Key.Select(k => $"{k.Key}={k.Value ?? "NULL"}"));

    public string Error => Row.Error;
}

/// <summary>
/// 실행·검증 단계(3단계): 표마다 진행률, 일시 정지·계속·중지, 끝나면 건수 비교와 실패 행, 「실패 행만 다시 넣기」, 「CSV로 저장」.
/// 중지는 실행기에 넘긴 취소 토큰으로 한다. CSV 는 <see cref="SaveCsvAsync"/> 를 불렀을 때만 쓴다.
/// </summary>
public sealed partial class RunViewModel(DataCopyServices services) : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private CopyRunControl? _control;
    private CopySessions? _sessions;
    private CopyJob? _job;
    private PrecheckReport? _report;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume), nameof(CanStop), nameof(CanRetry))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    private bool _isPaused;

    [ObservableProperty]
    private CopyOutcome? _outcome;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _readStartedAt;

    /// <summary>끝난 실행이 읽은 원본 시점(<see cref="CopyRunResult.ReadPoint"/>, 「SCN 1234」). 없으면 null.</summary>
    private string? _readPoint;

    /// <summary>아래 칸의 탭: 0 = 작업 로그, 1 = 실패한 행. 실행이 끝나 실패 행이 있으면 실패한 행 탭으로 옮긴다.</summary>
    [ObservableProperty]
    private int _detailTab;

    public ObservableCollection<TableRunRowViewModel> Rows { get; } = [];

    /// <summary>실행기가 실제로 보낸 문장·결과(작업 로그).</summary>
    public CopyRunLogViewModel Log { get; } = new();

    public ObservableCollection<FailedRowViewModel> FailedRows { get; } = [];

    public ObservableCollection<string> Notices { get; } = [];

    public bool CanPause => IsRunning && !IsPaused && _control is not null;

    public bool CanResume => IsRunning && IsPaused;

    public bool CanStop => IsRunning;

    public bool HasFailedRows => FailedRows.Count > 0;

    internal void NotifyFailedRowsChanged()
    {
        OnPropertyChanged(nameof(HasFailedRows));
        OnPropertyChanged(nameof(CanRetry));
    }

    /// <summary>끝난 실행에 실패 행이 있고, 세션·점검 결과가 남아 있을 때만.</summary>
    public bool CanRetry => !IsRunning && FailedRows.Count > 0 && _sessions is not null;

    /// <summary>
    /// 화면에 보이는 「읽기를 시작한 시각」 글자. 같은 시점 기준으로 읽었을 때 그 시점이다. 같은 시점 읽기로 동시에 복사했으면(PF2b) 읽은 원본 시점(「· SCN 1234」)을 붙인다.
    /// </summary>
    public string ReadStartedText => ReadStartedAt is { } at
        ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (_readPoint is { } point ? " · " + point : string.Empty)
        : string.Empty;

    /// <summary>복사를 실행한다. 끝나면(또는 중지되면) 결과를 화면에 채우고 작업의 마지막 실행 정보를 저장한다.</summary>
    public async Task StartAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(decisions);
        if (services.Runner is not { } runner)
        {
            StatusText = DataCopyText.RunnerMissing;
            return;
        }

        if (IsRunning)
        {
            return; // 이미 도는 중이면 겹쳐 시작하지 않는다
        }

        _sessions = sessions;
        SessionsAbandoned = false;
        _job = job;
        _report = report;
        Rows.Clear();
        FailedRows.Clear();
        Notices.Clear();
        Log.Clear();
        DetailTab = 0;
        Outcome = null;
        ReadStartedAt = null;
        _readPoint = null;
        // 진행·결과·건수 어림의 표 이름은 규칙 키다(같은 이름 표가 두 계정에 있어도 섞이지 않게). 그리드에는 계정.표를 보인다.
        foreach (var plan in report.Order.Where(p => p.Rule.Kind != RuleKind.Exclude))
        {
            var key = CopyOwners.Key(job, plan.Rule);
            Rows.Add(new TableRunRowViewModel(key)
            {
                Display = CopyOwners.SourceName(job, plan.Rule),
                Estimated = report.EstimatedRows.TryGetValue(key, out var n) ? n : null,
            });
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var control = new CopyRunControl();
        _cts = cts;
        _control = control;
        IsPaused = false;
        IsRunning = true;
        StatusText = "복사하는 중…";
        CopyRunResult? result = null;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = running.Task; // 레인 세션을 여는 동안 창을 닫아도 이 작업이 끝나기(레인 세션을 닫기)를 기다린다
        IReadOnlyList<CopySessions> workers = [];
        try
        {
            // 동시 테이블 복사(PF2): 레인 세션 쌍은 이번 실행에만 열고 끝나면 닫는다. _sessions 는 주 쌍 그대로다(다시 넣기는 한 테이블씩).
            workers = await OpenWorkersAsync(sessions, job, report, cts.Token).ConfigureAwait(true);
            var run = workers.Count > 0 ? sessions with { Workers = workers } : sessions;
            result = await runner.RunAsync(run, job, report, decisions, period, new UiProgress<CopyProgress>(OnProgress), control, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Outcome = CopyOutcome.Stopped;
            StatusText = DataCopyText.RunStopped;
        }
        catch (Exception e)
        {
            Outcome = CopyOutcome.Failed;
            StatusText = "복사를 마치지 못했습니다: " + e.Message;
            Log.Append("마무리", StatusText, CopyLogKind.Error);
        }
        finally
        {
            await DataCopySessions.CloseWorkersAsync(workers).ConfigureAwait(true); // 실행기가 레인 트랜잭션을 이미 끝냈다(되돌렸다)
            control.Resume();
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _control = null;
            }

            IsPaused = false;
            IsRunning = false;
        }

        try
        {
            if (result is not null)
            {
                Apply(result);
                NoteAbandoned(result);
                await SaveLastRunAsync(job, result, ct).ConfigureAwait(true);
            }

            OnPropertyChanged(nameof(CanRetry));
        }
        finally
        {
            running.TrySetResult();
        }
    }

    /// <summary>지금 도는(또는 마지막) 실행이 끝나면 완료되는 작업. 창을 닫을 때 세션을 닫기 전에 기다린다.</summary>
    public Task? Completion { get; private set; }

    /// <summary>
    /// 마지막 실행(또는 다시 넣기)에서 실행기가 돌아오지 않는 명령 때문에 세션을 버렸다(<see cref="CopyRunResult.SessionsAbandoned"/>, 리뷰 PF1-3).
    /// 그 세션은 더 쓸 수 없다: 「실패 행만 다시 넣기」를 막고(<see cref="CanRetry"/>), 창 쪽이 세션 쌍을 닫는다.
    /// </summary>
    public bool SessionsAbandoned { get; private set; }

    private void NoteAbandoned(CopyRunResult result)
    {
        SessionsAbandoned = result.SessionsAbandoned;
        if (SessionsAbandoned)
        {
            _sessions = null; // 버린 세션으로 다시 넣지 않는다(다시 점검하면 새 세션을 연다)
        }
    }

    /// <summary>작업 파일에 마지막 실행 정보를 저장할지(읽기 전용 작업 파일은 저장하지 않는다).</summary>
    public bool SaveLastRun { get; set; } = true;

    /// <summary>방금 끝난 실행의 요약. 열린 작업에 합쳐 다음 저장이 옛 값으로 덮어쓰지 않게 한다.</summary>
    public LastRunInfo? LastRun { get; private set; }

    /// <summary>
    /// 동시 테이블 복사(PF2)의 레인 세션 쌍을 연다: 실행기와 같은 판정(<see cref="CopyConcurrency.ForRun"/> — 작업 설정·점검 결과로 2 이상, 점검 결과에 동시 복사 확인 항목,
    /// 같은 시점 읽기면 시점 읽기 점검 항목)일 때만(아니면 실행기가 한 테이블씩 돌므로 열지 않는다). 적게 열렸으면 작업 로그에 남긴다. 여는 데 실패하면 로그에 남기고 레인 없이(한 테이블씩) 돈다.
    /// </summary>
    private async Task<IReadOnlyList<CopySessions>> OpenWorkersAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CancellationToken ct)
    {
        var decision = CopyConcurrency.ForRun(job, report, sessions.Source.Profile.Dbms);
        if (decision.Tables <= 1)
        {
            return [];
        }

        try
        {
            var opened = await DataCopySessions.OpenWorkersAsync(services, sessions.Source.Profile, sessions.Target.Profile, decision.Tables - 1, ct)
                .ConfigureAwait(true);
            if (opened.Note is { } note)
            {
                Log.Append("시작", note, CopyLogKind.Warning);
            }

            return opened.Pairs;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Append("시작", DataCopyText.L(WorkersFailed) + e.Message, CopyLogKind.Warning);
            return [];
        }
    }

    /// <summary>레인 세션을 열다 실패했을 때의 작업 로그 앞부분(뒤에 오류 글).</summary>
    internal const string WorkersFailed = "레인 세션을 열지 못해 한 테이블씩 복사합니다: ";

    private void OnProgress(CopyProgress p)
    {
        if (p.Log is { } entry)
        {
            Log.Append(entry); // 로그 줄은 진행 값을 바꾸지 않는다
            return;
        }

        if (p.Stage is { } stage)
        {
            StatusText = DataCopyText.StageText(stage); // 「동시 2개 · 단계 1/3」: 테이블 진행이 아니다
            return;
        }

        var row = Rows.FirstOrDefault(r => r.Table == p.Table);
        if (row is null)
        {
            return;
        }

        if (p.Phase == CopyRunner.PhaseLoadEnd)
        {
            row.EndLoad(p.Rows, p.Written); // 실제로 읽은 수가 기준이 된다: 막대는 이 시점에 찬다(결과를 기다리지 않는다)
        }
        else if (p.Phase is CopyRunner.PhaseLoad or CopyRunner.PhaseRetry)
        {
            // 진행 값(읽은 수·넣은 수·예상 건수)은 적재(와 다시 넣기) 보고만 정한다. 적재가 끝난 테이블에 늦게 온 적재 보고는 값을 되돌리지 않는다.
            if (!row.LoadEnded || p.Phase == CopyRunner.PhaseRetry)
            {
                row.Rows = p.Rows;
                row.Estimated = p.Estimated ?? row.Estimated;
                row.Phase = p.Phase;
                if (p.Written is { } written)
                {
                    row.Written = written;
                }
            }
        }
        else if (row.LoadEnded)
        {
            // 적재를 마친 테이블에 시퀀스·다시 켜기·건수 비교 같은 마무리 보고가 온다(읽은 수 0 등): 단계 글만 바꾼다.
            if (RunPhases.Finishing(p.Phase) is { } finishing)
            {
                row.Phase = finishing;
            }
        }
        else
        {
            // 끄기·정리·다시 만들기·되살리기 같은 보고도 단계 글만 바꾼다(적재 전이면 읽은 수는 어차피 0, 적재 중 멈춘 테이블의 값은 남긴다).
            row.Phase = p.Phase;
        }

        if (p.Waits is { } waits)
        {
            // 빈 목록 = 멈춤이 풀렸다. null 이면(보통 진행 보고) 앞 표시를 둔다: 다른 쪽(원본·대상)이 아직 기다릴 수 있다.
            row.WaitText = string.Join(" / ", waits.Select(DataCopyText.WaitText));
        }
    }

    // 실행기 결과로 표를 맞춘다. 진행 보고가 늦게 도착해도 최종 값은 결과가 정한다.
    private void Apply(CopyRunResult result)
    {
        foreach (var table in result.Tables)
        {
            var row = Rows.FirstOrDefault(r => r.Table == table.Table);
            if (row is null)
            {
                row = new TableRunRowViewModel(table.Table) { Display = Display(table.Table) };
                Rows.Add(row);
            }

            // 끝까지 읽은 테이블(성공·일부 실패)은 실제로 읽은 수가 기준이다(점검 때 센 예상 건수가 어긋나도 막대는 가득 찬다: Percent). 아니면 예상 건수가 없을 때만 채운다.
            if (table.Outcome is CopyOutcome.Succeeded or CopyOutcome.PartiallyFailed)
            {
                row.Estimated = table.SourceRows;
            }
            else
            {
                row.Estimated ??= table.SourceRows;
            }

            row.SourceRows = table.SourceRows;
            row.TargetRows = table.TargetRows >= 0 ? table.TargetRows : null; // -1 = 대상 건수 모름(중지 등): 그리드에서 「-」, 건수 비교 없음
            row.Outcome = table.Outcome;
            row.Message = table.Message ?? string.Empty;
            row.Phase = table.Outcome == CopyOutcome.Succeeded ? "완료" : "문제 있음";
            row.WaitText = string.Empty;
            row.TimesText = table.Times is { } times ? DataCopyText.TimesText(times) : string.Empty;
            row.Rows = table.SourceRows;
        }

        FailedRows.Clear();
        foreach (var failed in result.FailedRows)
        {
            FailedRows.Add(new FailedRowViewModel(failed, Display(failed.Table)));
        }

        Notices.Clear();
        foreach (var notice in result.Notices)
        {
            Notices.Add(notice);
        }

        ReadStartedAt = result.ReadStartedAt;
        _readPoint = result.ReadPoint;
        OnPropertyChanged(nameof(ReadStartedText));
        if (FailedRows.Count > 0)
        {
            DetailTab = 1;
        }

        Outcome = result.Outcome;
        StatusText = result.Outcome switch
        {
            CopyOutcome.Succeeded => "복사를 마쳤습니다.",
            CopyOutcome.PartiallyFailed => "복사를 마쳤지만 실패한 행이 있습니다.",
            CopyOutcome.Stopped => DataCopyText.RunStopped,
            _ => "복사에 실패했습니다.",
        };
    }

    // 규칙 키 → 그리드의 계정.표(작업에 없는 키면 그대로).
    private string Display(string key) => _job is { } job ? PrecheckViewModel.TableText(job, key) : key;

    private async Task SaveLastRunAsync(CopyJob job, CopyRunResult result, CancellationToken ct)
    {
        // 대상 건수를 세지 못한 표(중지·예외로 건수 비교를 건너뛴 표)는 -1(모름)이다. 합에 넣으면 행 수가 음수가 될 수 있다.
        // 모든 표가 모름이면 행 수를 0 이나 음수로 둘 수 없는데 마지막 실행 요약에는 모름을 담을 자리가 없으므로 요약을 남기지 않는다.
        var known = result.Tables.Where(t => t.TargetRows >= 0).ToList();
        if (known.Count == 0 && result.Tables.Count > 0)
        {
            LastRun = null;
            return;
        }

        var rows = known.Sum(t => t.TargetRows);
        LastRun = new LastRunInfo(services.Time.GetUtcNow(), result.Outcome, rows);
        if (!SaveLastRun || services.Store is not { } store)
        {
            return;
        }

        try
        {
            await store.SaveAsync(job with { LastRun = LastRun }, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notices.Add("마지막 실행 정보를 저장하지 못했습니다: " + e.Message);
        }
    }

    /// <summary>다른 작업을 열 때: 이전 실행 결과(표·실패 행·상태)를 비운다. 도는 중이면 호출하지 않는다.</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            return;
        }

        Rows.Clear();
        FailedRows.Clear();
        Notices.Clear();
        Log.Clear();
        DetailTab = 0;
        Outcome = null;
        ReadStartedAt = null;
        _readPoint = null;
        StatusText = string.Empty;
        LastRun = null;
        SessionsAbandoned = false;
        _sessions = null;
        _job = null;
        _report = null;
        OnPropertyChanged(nameof(ReadStartedText));
        OnPropertyChanged(nameof(CanRetry));
    }

    public void Pause()
    {
        if (CanPause && _control is { } control)
        {
            control.Pause();
            IsPaused = true;
            StatusText = "일시 정지했습니다. 지금 배치가 끝난 뒤에 멈춥니다.";
        }
    }

    public void Resume()
    {
        if (CanResume && _control is { } control)
        {
            control.Resume();
            IsPaused = false;
            StatusText = "복사하는 중…";
        }
    }

    /// <summary>중지: 현재 배치를 되돌리고 끈 트리거·FK 를 다시 켠 뒤 끝낸다(실행기가 한다). 일시 정지 중이면 먼저 풀어 취소가 닿게 한다.</summary>
    public void Stop()
    {
        if (_cts is { } cts)
        {
            StatusText = "중지하는 중…";
            cts.Cancel();
            _control?.Resume();
        }
    }

    /// <summary>실패 행의 키로 원본을 다시 읽어 넣는다(설계 8.2). 성공한 행은 목록에서 빠지고 결과는 표에 합쳐진다.</summary>
    public async Task RetryFailedAsync(CancellationToken ct = default)
    {
        if (!CanRetry || _sessions is null || _job is null || _report is null)
        {
            return;
        }

        if (services.Runner is not { } runner)
        {
            StatusText = DataCopyText.RunnerMissing;
            return;
        }

        var rows = FailedRows.Select(f => f.Row).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        _control = null; // 다시 넣기에는 일시 정지가 없다(중지만)
        SessionsAbandoned = false;
        DetailTab = 0; // 다시 넣기 로그는 앞 실행 로그 뒤에 이어 붙인다
        IsRunning = true;
        StatusText = "실패 행을 다시 넣는 중…";
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = running.Task;
        try
        {
            var result = await runner.RetryFailedRowsAsync(_sessions, _job, _report, rows, new UiProgress<CopyProgress>(OnProgress), cts.Token).ConfigureAwait(true);
            FailedRows.Clear();
            foreach (var failed in result.FailedRows)
            {
                FailedRows.Add(new FailedRowViewModel(failed, Display(failed.Table)));
            }

            foreach (var table in result.Tables)
            {
                if (Rows.FirstOrDefault(r => r.Table == table.Table) is { } row)
                {
                    var sent = rows.Count(r => r.Table == table.Table);
                    var remaining = result.FailedRows.Count(f => f.Table == table.Table);
                    row.TargetRows = AddRetriedRows(row.TargetRows, table.TargetRows, sent, remaining);
                    row.Message = table.Message ?? string.Empty;

                    // 실패 행이 모두 들어갔으면 표 줄도 성공으로 바꾼다(남았으면 그대로). 표 전체가 실패한 줄은 건드리지 않는다.
                    if (row.Outcome == CopyOutcome.PartiallyFailed && !result.FailedRows.Any(f => f.Table == table.Table))
                    {
                        row.Outcome = CopyOutcome.Succeeded;
                        row.Phase = "완료";
                    }
                }
            }

            foreach (var notice in result.Notices)
            {
                Notices.Add(notice);
            }

            if (result.FailedRows.Count > 0)
            {
                DetailTab = 1;
            }

            Outcome = result.FailedRows.Count == 0 ? CopyOutcome.Succeeded : CopyOutcome.PartiallyFailed;
            StatusText = result.FailedRows.Count == 0 ? "실패했던 행을 모두 넣었습니다." : $"실패한 행이 {result.FailedRows.Count:N0}개 남았습니다.";
            NoteAbandoned(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = DataCopyText.RunStopped;
        }
        catch (Exception e)
        {
            StatusText = "실패 행을 다시 넣지 못했습니다: " + e.Message;
            Log.Append("마무리", StatusText, CopyLogKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            IsRunning = false;
            OnPropertyChanged(nameof(CanRetry));
            running.TrySetResult();
        }
    }

    /// <summary>
    /// 다시 넣기 결과의 <c>TargetRows</c> 는 기간 조건 없이 키로 읽어 이번에 넣은 행 수다(실행기가 대상 건수를 다시 세지 않는다).
    /// 그래서 앞선 대상 건수에 더해 원본·대상 건수 비교를 이어 간다. 앞선 건수를 모르면(없거나 음수) 그대로 둔다.
    /// 실행기는 커밋하지 못해 되돌린 표의 넣은 수도 담고 그 표의 입력 행을 모두 실패 행으로 돌려주므로,
    /// 「넣은 수 + 남은 실패 수 = 보낸 수」가 맞을 때만(확정된 표) 더한다.
    /// </summary>
    internal static long? AddRetriedRows(long? before, long retried, int sent, int remaining) =>
        before is >= 0 && retried >= 0 && retried + remaining == sent ? before.Value + retried : before;

    /// <summary>
    /// 「CSV로 저장」: 사용자가 누를 때만 호출한다. <paramref name="open"/> 이 저장 위치를 물어 스트림을 열어 주고, 취소하면 null 을 돌려준다.
    /// 실행이 끝났을 때 자동으로 쓰는 경로는 없다.
    /// </summary>
    public async Task SaveCsvAsync(Func<Task<Stream?>> open, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (FailedRows.Count == 0)
        {
            return;
        }

        var stream = await open().ConfigureAwait(true);
        if (stream is null)
        {
            return;
        }

        await using (stream.ConfigureAwait(true))
        {
            await FailedRowsCsv.WriteAsync(stream, FailedRows.Select(f => f.Row).ToList(), ct).ConfigureAwait(true);
        }
    }
}
