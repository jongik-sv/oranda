using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>데이터 복사 창의 3단계.</summary>
public enum DataCopyStep
{
    RuleEdit,
    Precheck,
    Run,
}

/// <summary>지난 실행이 끝나지 않아 꺼 둔 채로 남았을 수 있는 트리거·FK 한 묶음(설계 9장).</summary>
public sealed record UnfinishedRunItem(UnfinishedRun Run)
{
    public string Header => $"{Run.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} 에 시작한 실행";

    public IReadOnlyList<string> Lines => [.. Run.Disabled.Select(d => $"{(d.Kind == DisabledKind.Trigger ? "트리거" : "FK")} {d.Owner}.{d.Table}.{d.Name}")];
}

/// <summary>
/// 데이터 복사 창의 뷰모델: 내 작업 목록, 규칙 편집(1단계) → 사전 점검(2단계) → 실행·검증(3단계)을 잇는다.
/// 서비스가 없으면(다른 레인의 구현이 아직 등록되지 않았으면) 예외 대신 <see cref="StatusText"/> 로 알린다.
/// 원본·대상 세션은 처음 점검할 때 한 번 열어 창이 닫힐 때까지 쓴다.
/// </summary>
public sealed partial class DataCopyViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly DataCopyServices _services;
    private CopySessions? _sessions;
    private ISession? _metadataSession;
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    /// <summary>중지 뒤 실행기가 정리(끈 트리거·FK 다시 켜기)를 마칠 때까지 기다리는 상한. 시험이 줄일 수 있다.</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>창을 닫은 뒤에도 사용자에게 알려야 할 경고(상태 줄로 보낸다). 없으면 null.</summary>
    public string? ClosingWarning { get; private set; }

    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private bool _disposed;
    private bool _readOnlyJob;
    private ResolvedPeriod? _checkedPeriod;
    private CopyJob? _checkedJob;
    private ConnectionProfile? _checkedSource;
    private ConnectionProfile? _checkedTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRuleEdit), nameof(IsPrecheck), nameof(IsRun))]
    private DataCopyStep _step = DataCopyStep.RuleEdit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob), nameof(CanGoToPrecheck))]
    private CopyJob? _currentJob;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToPrecheck), nameof(CanStartRun))]
    private bool _isBusy;

    public DataCopyViewModel(DataCopyServices services)
    {
        _services = services;
        Jobs = new JobListViewModel(services);
        Editor = new RuleEditorViewModel(services);
        Precheck = new PrecheckViewModel(services);
        Run = new RunViewModel(services);
        Editor.PropertyChanged += OnEditorChanged;
        UnfinishedRuns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUnfinished));
        Run.FailedRows.CollectionChanged += (_, _) => Run.NotifyFailedRowsChanged();
        Precheck.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrecheckViewModel.CanRun))
            {
                OnPropertyChanged(nameof(CanStartRun));
            }
        };
        Run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(CanGoToPrecheck));
                OnPropertyChanged(nameof(CanStartRun));
            }
        };
    }

    public JobListViewModel Jobs { get; }

    public RuleEditorViewModel Editor { get; }

    public PrecheckViewModel Precheck { get; }

    public RunViewModel Run { get; }

    public ObservableCollection<UnfinishedRunItem> UnfinishedRuns { get; } = [];

    public bool HasJob => CurrentJob is not null;

    /// <summary>점검을 시작할 수 있는지: 열린 작업이 있고, 다른 점검·세션 열기·실행이 도는 중이 아니며, 대상이 읽기 전용이 아닐 때.</summary>
    public bool CanGoToPrecheck => HasJob && !IsBusy && !Run.IsRunning && !Editor.HasTargetReadOnlyNotice;

    /// <summary>「복사 실행」: 점검이 통과했고 다른 점검·세션 열기·실행이 도는 중이 아닐 때.</summary>
    public bool CanStartRun => Precheck.CanRun && !IsBusy && !Run.IsRunning;

    public bool HasUnfinished => UnfinishedRuns.Count > 0;

    public bool IsRuleEdit => Step == DataCopyStep.RuleEdit;

    public bool IsPrecheck => Step == DataCopyStep.Precheck;

    public bool IsRun => Step == DataCopyStep.Run;

    /// <summary>원본·대상 세션이 열려 있는지.</summary>
    public bool HasSessions => _sessions is not null;

    // 이미 세션이 열려 있으면 규칙을 고를 때 열 정보를 자동으로 읽는다. 열려 있지 않으면 비밀번호를 묻게 되므로 「열 정보 읽기」를 눌렀을 때만 읽는다.
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuleEditorViewModel.HasTargetReadOnlyNotice))
        {
            OnPropertyChanged(nameof(CanGoToPrecheck)); // 읽기 전용 대상을 고르면 다음 단계 단추가 꺼진다
        }

        if (e.PropertyName == nameof(RuleEditorViewModel.SelectedRule) && Editor.SelectedRule is { HasColumns: false } rule
            && (_sessions is not null || _metadataSession is not null))
        {
            _ = LoadColumnsQuietlyAsync(rule);
        }
    }

    private async Task LoadColumnsQuietlyAsync(TableRuleViewModel rule)
    {
        try
        {
            await LoadColumnsAsync(rule).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Say("열 정보를 읽지 못했습니다: " + e.Message, error: true);
        }
    }

    private void Say(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    /// <summary>창을 처음 열 때: 접속 목록과 내 작업 목록을 읽는다.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _profiles = await _services.LoadProfilesAsync(ct).ConfigureAwait(true);
        Editor.SetConnections(_profiles);
        await Jobs.RefreshAsync(ct).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(Jobs.Notice))
        {
            Say(Jobs.Notice, error: true);
        }
    }

    // 접속 관리자에서 환경 라벨(운영 등)이 바뀌었을 수 있으므로 세션을 열기 전에 목록을 다시 읽고 고른 접속을 같은 접속의 새 값으로 바꾼다.
    private async Task RefreshProfilesAsync(CancellationToken ct)
    {
        _profiles = await _services.LoadProfilesAsync(ct).ConfigureAwait(true);
        Editor.SetConnections(_profiles);
    }

    private bool RunInProgress()
    {
        if (!Run.IsRunning)
        {
            return false;
        }

        Say("복사가 도는 중입니다. 끝나거나 중지한 뒤에 바꾸세요.", error: true);
        return true;
    }

    /// <summary>
    /// 작업을 연다. 저장된 접속 참조를 이 PC 의 접속에 다시 잇는다(같은 ID·이름 → 같은 이름). 대상이 이 PC 에서 운영 접속이면
    /// 대상을 비우고 알린다(동료가 보낸 작업으로 운영 접속이 대상이 되는 길을 막는다). 끝나지 않은 지난 실행이 있으면 목록으로 보인다.
    /// </summary>
    public async Task OpenJobAsync(CopyJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (RunInProgress())
        {
            Jobs.Selected = Jobs.Items.FirstOrDefault(i => i.Job.Id == CurrentJob?.Id); // 목록 선택을 도는 작업으로 되돌린다
            return;
        }

        await RefreshProfilesAsync(ct).ConfigureAwait(true);
        await CloseSessionsAsync().ConfigureAwait(true);
        Precheck.Reset();
        Run.Reset();
        _checkedPeriod = null;
        _readOnlyJob = Jobs.Items.FirstOrDefault(i => i.Job.Id == job.Id)?.IsReadOnly ?? false;
        Run.SaveLastRun = !_readOnlyJob;
        var source = ConnectionRelinker.Resolve(job.Source, _profiles, isTarget: false);
        var target = ConnectionRelinker.Resolve(job.Target, _profiles, isTarget: true);
        var notices = new List<string>();
        if (source.Profile is null && !string.IsNullOrEmpty(job.Source.Name))
        {
            notices.Add(DataCopyText.SourceNotFound(job.Source.Name));
        }

        if (target.Kind == RelinkKind.BlockedProduction)
        {
            notices.Add(DataCopyText.TargetBlocked(job.Target.Name));
        }
        else if (target.Profile is null && !string.IsNullOrEmpty(job.Target.Name))
        {
            notices.Add(DataCopyText.TargetNotFound(job.Target.Name));
        }

        CurrentJob = job;
        Editor.Load(job, source.Profile, target.Profile, string.Join(' ', notices));
        Step = DataCopyStep.RuleEdit;
        Say(string.Join(' ', notices), error: notices.Count > 0);
        await LoadUnfinishedAsync(job, ct).ConfigureAwait(true);
    }

    private async Task LoadUnfinishedAsync(CopyJob job, CancellationToken ct)
    {
        UnfinishedRuns.Clear();
        if (_services.RunLog is not { } log)
        {
            return;
        }

        foreach (var run in await log.FindUnfinishedAsync(job.Id, ct).ConfigureAwait(true))
        {
            UnfinishedRuns.Add(new UnfinishedRunItem(run));
        }
    }

    /// <summary>
    /// 「다시 켜기」: 대상 세션에서 꺼 두었던 FK·트리거의 ENABLE 문장을 실행하고 그 기록을 복구된 것으로 표시한다.
    /// 문장이 하나라도 실패하면 표시하지 않고 실패한 이유를 알린다(다시 눌러 재시도할 수 있다).
    /// </summary>
    public async Task ReenableAsync(UnfinishedRunItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (RunInProgress())
        {
            return;
        }

        if (CurrentJob is null)
        {
            return;
        }

        // 지난 실행이 쓴 대상 = 작업에 저장된 대상 접속. 편집 화면에서 바꾸었지만 아직 저장하지 않은 대상에는 켜지 않는다.
        // 접속 목록을 다시 읽어 환경 라벨이 바뀌었는지(운영으로) 확인한 뒤 연다.
        await RefreshProfilesAsync(ct).ConfigureAwait(true);
        var relinked = ConnectionRelinker.Resolve(CurrentJob.Target, _profiles, isTarget: true);
        if (relinked.Kind == RelinkKind.BlockedProduction)
        {
            Say(DataCopyText.ProductionTarget, error: true); // 운영 접속에는 세션도 열지 않는다
            return;
        }

        if (relinked.Profile is not { } target || !CopyTargetGuard.IsAllowedTarget(target))
        {
            Say(DataCopyText.TargetMissing, error: true);
            return;
        }

        if (_services.RunLog is not { } log)
        {
            Say(DataCopyText.RunLogMissing, error: true);
            return;
        }

        IsBusy = true;
        try
        {
            var reuse = _sessions is { } pair && SameConnection(pair.Target, target) ? pair.Target : null;
            var session = reuse ?? await _services.OpenSessionAsync(target, ct).ConfigureAwait(true);
            if (session is null)
            {
                Say(DataCopyText.SessionsNotOpened, error: true);
                return;
            }

            var ownsSession = reuse is null;
            try
            {
                // 외래키는 부모 없는 행이 있으면 VALIDATE 가 실패하므로, 먼저 켜고 안 되면 NOVALIDATE 로 켠다.
                var failures = new List<string>();
                foreach (var disabled in item.Run.Disabled)
                {
                    var error = await EnableAsync(session, disabled, ct).ConfigureAwait(true);
                    if (error is not null)
                    {
                        failures.Add($"{disabled.Name}: {error}");
                    }
                }

                if (failures.Count > 0)
                {
                    Say("다시 켜지 못한 항목이 있습니다: " + string.Join(" / ", failures), error: true);
                    return;
                }

                await log.MarkRecoveredAsync(item.Run.RunFile, ct).ConfigureAwait(true);
                UnfinishedRuns.Remove(item);
                Say("꺼 두었던 트리거·FK 를 다시 켰습니다.");
            }
            finally
            {
                if (ownsSession)
                {
                    await ConnectionManager.ConnectionService.CloseAsync(session).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> EnableAsync(ISession session, DisabledObject disabled, CancellationToken ct)
    {
        var owner = QuoteName(disabled.Owner);
        if (disabled.Kind == DisabledKind.Trigger)
        {
            return await _services.RunStatementAsync(session, $"ALTER TRIGGER {owner}.{QuoteName(disabled.Name)} ENABLE", ct).ConfigureAwait(true);
        }

        var alter = $"ALTER TABLE {owner}.{QuoteName(disabled.Table)}";
        var error = await _services.RunStatementAsync(session, $"{alter} ENABLE VALIDATE CONSTRAINT {QuoteName(disabled.Name)}", ct).ConfigureAwait(true);
        return error is null
            ? null
            : await _services.RunStatementAsync(session, $"{alter} ENABLE NOVALIDATE CONSTRAINT {QuoteName(disabled.Name)}", ct).ConfigureAwait(true);
    }

    private static string QuoteName(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// 스키마 브라우저 「데이터 복사에 추가」: 열려 있는 작업에 표 규칙을 더한다. 열린 작업이 없으면 새 작업을 만들고,
    /// 원본 접속이 비어 있으면 표를 고른 접속(<paramref name="sourceProfile"/>)을 원본으로 둔다.
    /// </summary>
    public async Task AddTableAsync(DbObjectRef table, ConnectionProfile? sourceProfile = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (RunInProgress())
        {
            return; // 도는 동안에는 화면을 바꾸지 않는다
        }

        if (CurrentJob is null)
        {
            var created = await Jobs.NewAsync("새 작업 " + _services.Time.GetLocalNow().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(true);
            if (created is null)
            {
                Say(Jobs.Notice, error: true);
                return;
            }

            await OpenJobAsync(created, ct).ConfigureAwait(true);
        }

        if (Editor.Source is null && sourceProfile is not null)
        {
            Editor.Source = Editor.SourceOptions.FirstOrDefault(o => o.Profile.Id == sourceProfile.Id && o.Profile.Name == sourceProfile.Name);
        }

        Step = DataCopyStep.RuleEdit;
        var session = await SourceSessionAsync(ct).ConfigureAwait(true);
        await Editor.AddTableAsync(table, session, ct).ConfigureAwait(true);
        Say(Editor.Notice.Length > 0 ? Editor.Notice : $"{table.Name} 규칙을 추가했습니다.", Editor.Notice.Length > 0);
    }

    /// <summary>1단계 → 2단계: 입력을 확인하고 작업을 저장한 뒤 세션을 열어 사전 점검을 돌린다.</summary>
    public async Task GoToPrecheckAsync(CancellationToken ct = default)
    {
        if (CurrentJob is null || IsBusy || Run.IsRunning)
        {
            return; // 점검·세션 열기가 도는 중에 겹쳐 시작하지 않는다
        }

        IsBusy = true;
        try
        {
            Precheck.Invalidate(); // 입력 확인이 실패해 일찍 끝나도 옛 점검 결과로는 실행할 수 없다
            _checkedJob = null;
            await RefreshProfilesAsync(ct).ConfigureAwait(true); // 접속의 환경 라벨은 열어 둔 사이에 바뀔 수 있다
            if (Editor.Validate() is { } problem)
            {
                Say(problem, error: true);
                return;
            }

            var job = Editor.BuildJob();
            CurrentJob = job;
            if (_services.Store is { } store && !_readOnlyJob)
            {
                await store.SaveAsync(job, ct).ConfigureAwait(true);
            }

            if (await EnsureSessionsAsync(ct).ConfigureAwait(true) is not { } sessions)
            {
                return;
            }

            if (Run.IsRunning)
            {
                return; // 세션을 여는 사이에 실행이 시작됐다면 점검으로 바꾸지 않는다
            }

            Step = DataCopyStep.Precheck;
            Say("사전 점검 중…");
            _checkedPeriod = Editor.ResolvePeriod(); // 실행도 점검한 기간 그대로 쓴다(자정을 넘겨도 범위가 바뀌지 않게)
            _checkedJob = job; // 점검한 작업·접속의 지문(실행·재시도 직전에 같은지 본다)
            _checkedSource = Editor.Source!.Profile;
            _checkedTarget = Editor.Target!.Profile;
            await Precheck.RunAsync(sessions, job, _checkedPeriod, ct).ConfigureAwait(true);
            Say(Precheck.Notice.Length > 0 ? Precheck.Notice : Precheck.ProgressText, Precheck.HasErrors || Precheck.Notice.Length > 0);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<CopySessions?> EnsureSessionsAsync(CancellationToken ct)
    {
        if (Editor.Source is not { } source || Editor.Target is not { } target)
        {
            Say(Editor.Source is null ? DataCopyText.SourceMissing : DataCopyText.TargetMissing, error: true);
            return null;
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_sessions is not null)
            {
                if (SameConnection(_sessions.Source, source.Profile, readOnlySource: true) && SameConnection(_sessions.Target, target.Profile))
                {
                    return _sessions;
                }

                await CloseSessionPairAsync().ConfigureAwait(true); // 접속을 바꿨으면 이전 세션은 쓰지 않는다
            }

            await CloseMetadataSessionAsync().ConfigureAwait(true);
            CopySessions? opened;
            try
            {
                opened = await DataCopySessions.OpenAsync(_services.OpenSessionAsync, source.Profile, target.Profile, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Say("세션을 열지 못했습니다: " + e.Message, error: true);
                return null;
            }

            if (opened is null)
            {
                Say(DataCopyText.SessionsNotOpened, error: true);
                return null;
            }

            if (_disposed)
            {
                await DataCopySessions.CloseAsync(opened).ConfigureAwait(true); // 여는 사이에 창이 닫혔다
                return null;
            }

            _sessions = opened;
            OnPropertyChanged(nameof(HasSessions));
            return _sessions;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 열린 세션이 이 프로필로 연 것인지. 접속 ID·이름뿐 아니라 사용자·주소·환경 라벨·읽기 전용까지 같아야 한다
    /// (정책 관문은 세션이 열릴 때 받은 프로필 스냅숏으로 판단하므로, 바뀐 프로필에 옛 세션을 쓰지 않는다). 원본 세션은 늘 읽기 전용으로 연다.
    /// </summary>
    private static bool SameConnection(ISession session, ConnectionProfile profile, bool readOnlySource = false) =>
        session.Profile.Id == profile.Id && string.Equals(session.Profile.Name, profile.Name, StringComparison.Ordinal)
        && string.Equals(session.Profile.User, profile.User, StringComparison.Ordinal)
        && string.Equals(session.Profile.DataSource, profile.DataSource, StringComparison.Ordinal)
        && session.Profile.Environment == profile.Environment
        && session.Profile.ReadOnly == (readOnlySource || profile.ReadOnly);

    private static bool SameSnapshot(ConnectionProfile a, ConnectionProfile b) =>
        a.Id == b.Id && string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.User, b.User, StringComparison.Ordinal)
        && string.Equals(a.DataSource, b.DataSource, StringComparison.Ordinal) && a.Environment == b.Environment && a.ReadOnly == b.ReadOnly;

    /// <summary>
    /// 표 열 정보를 읽을 원본 세션. 점검용 세션 쌍이 열려 있고 원본이 같으면 그것을 쓰고, 아니면 원본만 읽기 전용으로 따로 연다
    /// (표를 추가하거나 「열 정보 읽기」를 누를 때만 연다). 원본을 고르지 않았거나 사용자가 비밀번호 입력을 취소하면 null.
    /// </summary>
    private async Task<ISession?> SourceSessionAsync(CancellationToken ct)
    {
        if (Editor.Source is not { } source)
        {
            return null;
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_sessions is { } pair && SameConnection(pair.Source, source.Profile, readOnlySource: true))
            {
                return pair.Source;
            }

            if (_metadataSession is { } open)
            {
                if (SameConnection(open, source.Profile, readOnlySource: true))
                {
                    return open;
                }

                await CloseMetadataSessionAsync().ConfigureAwait(true);
            }

            ISession? opened = null;
            try
            {
                opened = await _services.OpenSessionAsync(source.Profile with { ReadOnly = true }, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Say("원본 세션을 열지 못했습니다: " + e.Message, error: true);
            }

            if (opened is not null && _disposed)
            {
                await ConnectionManager.ConnectionService.CloseAsync(opened).ConfigureAwait(true); // 여는 사이에 창이 닫혔다
                return null;
            }

            _metadataSession = opened;
            return opened;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CloseMetadataSessionAsync()
    {
        var session = _metadataSession;
        _metadataSession = null;
        if (session is not null)
        {
            await ConnectionManager.ConnectionService.CloseAsync(session).ConfigureAwait(true);
        }
    }

    /// <summary>선택한 규칙의 표 열 정보를 원본에서 읽는다(저장된 작업을 열면 규칙에는 열 정보가 없다).</summary>
    public async Task LoadColumnsAsync(TableRuleViewModel rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var session = await SourceSessionAsync(ct).ConfigureAwait(true);
        if (session is null)
        {
            Say(Editor.Source is null ? DataCopyText.SourceMissing : DataCopyText.SessionsNotOpened, error: true);
            return;
        }

        var owner = string.IsNullOrWhiteSpace(Editor.SourceOwner) ? session.Profile.User.ToUpperInvariant() : Editor.SourceOwner;
        await Editor.LoadColumnsAsync(rule, new DbObjectRef(owner, rule.Table, "TABLE"), session, ct).ConfigureAwait(true);
        Say(Editor.Notice.Length > 0 ? Editor.Notice : $"{rule.Table} 의 열 정보를 읽었습니다.", Editor.Notice.Length > 0);
    }

    /// <summary>
    /// 점검 선택지 중 규칙을 바꾸는 것을 적용하고 점검을 다시 한다. 점검 결과를 먼저 지운 뒤 작업을 바꾸고 저장한다
    /// (저장이 실패하거나 오래 걸려도 옛 점검 결과의 문장으로 바뀐 작업을 실행하지 않게).
    /// </summary>
    public async Task ApplyChoiceAsync(CheckItemViewModel item, CheckChoice choice, CancellationToken ct = default)
    {
        if (CurrentJob is null || _sessions is null || IsBusy || Precheck.IsRunning || Run.IsRunning
            || PrecheckViewModel.ApplyJobChoice(CurrentJob, item, choice) is not { } changed)
        {
            return;
        }

        await ApplyJobChangeAsync(changed, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 「연결」 체크: 시퀀스 규칙을 작업에 넣거나 빼고 저장한 뒤 점검을 다시 한다(연결한 규칙은 점검의 시퀀스 점검 대상이 된다).
    /// 지금 바꿀 수 없는 상태(점검·실행이 도는 중, 세션 없음)면 체크를 원래대로 돌린다.
    /// </summary>
    public async Task SetSequenceLinkAsync(SequenceLinkViewModel link, bool linked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (CurrentJob is null || _sessions is null || IsBusy || Precheck.IsRunning || Run.IsRunning
            || PrecheckViewModel.ApplySequenceLink(CurrentJob, link.Rule, linked) is not { } changed)
        {
            link.IsLinked = !linked;
            return;
        }

        await ApplyJobChangeAsync(changed, ct).ConfigureAwait(true);
    }

    // 점검 결과를 먼저 지운 뒤 바뀐 작업을 저장하고 점검을 다시 한다(저장이 실패해도 옛 점검 결과로 실행하지 않는다).
    private async Task ApplyJobChangeAsync(CopyJob changed, CancellationToken ct)
    {
        if (_sessions is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Precheck.Invalidate();
            _checkedJob = null;
            CurrentJob = changed;
            Editor.Load(changed, Editor.Source?.Profile, Editor.Target?.Profile, Editor.Notice);
            if (_services.Store is { } store && !_readOnlyJob)
            {
                try
                {
                    await store.SaveAsync(changed, ct).ConfigureAwait(true);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Say("작업을 저장하지 못했습니다: " + e.Message, error: true);
                    return;
                }
            }

            _checkedPeriod = Editor.ResolvePeriod();
            _checkedJob = changed;
            await Precheck.RunAsync(_sessions, changed, _checkedPeriod, ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>규칙 편집으로 돌아간다. 실행이 도는 동안에는 돌아가지 않는다(먼저 중지한다).</summary>
    public void BackToRuleEdit()
    {
        if (RunInProgress())
        {
            return;
        }

        Precheck.Cancel();
        Step = DataCopyStep.RuleEdit;
    }

    /// <summary>
    /// 실행·재시도 직전 확인: 점검한 작업·접속이 지금도 그대로인지 본다. 접속 목록을 다시 읽어 대상이 운영 접속이 되었거나
    /// 환경 라벨·읽기 전용·사용자·주소가 바뀌었거나 작업이 바뀌었으면 실행하지 않고 점검 결과를 지운다. 같으면 열려 있는 세션을 돌려준다.
    /// </summary>
    private async Task<CopySessions?> PreflightAsync(CancellationToken ct)
    {
        string? reason = null;
        if (_sessions is null || CurrentJob is null || _checkedJob is null || _checkedSource is null || _checkedTarget is null
            || !ReferenceEquals(_checkedJob, CurrentJob))
        {
            reason = "점검한 뒤 작업이 바뀌었습니다.";
        }
        else
        {
            await RefreshProfilesAsync(ct).ConfigureAwait(true);
            var source = _profiles.FirstOrDefault(p => p.Id == _checkedSource.Id && p.Name == _checkedSource.Name);
            var target = _profiles.FirstOrDefault(p => p.Id == _checkedTarget.Id && p.Name == _checkedTarget.Name);
            if (target is not null && !CopyTargetGuard.IsAllowedTarget(target))
            {
                reason = DataCopyText.ProductionTarget;
            }
            else if (source is null || target is null || !SameSnapshot(source, _checkedSource) || !SameSnapshot(target, _checkedTarget)
                || !SameConnection(_sessions.Source, source, readOnlySource: true) || !SameConnection(_sessions.Target, target))
            {
                reason = "점검한 뒤 접속 설정이 바뀌었습니다.";
            }
        }

        if (reason is null)
        {
            return _sessions;
        }

        Precheck.Invalidate();
        _checkedJob = null;
        await CloseSessionPairAsync().ConfigureAwait(true); // 바뀐 접속에는 옛 세션을 쓰지 않는다
        Say(reason + " 다시 점검하세요.", error: true);
        return null;
    }

    /// <summary>「실패 행만 다시 넣기」: 실행과 같은 직전 확인을 거친다.</summary>
    public async Task RetryFailedRowsAsync(CancellationToken ct = default)
    {
        if (IsBusy || Run.IsRunning || !Run.CanRetry)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await PreflightAsync(ct).ConfigureAwait(true) is null)
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await Run.RetryFailedAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 2단계 → 3단계: 「복사 실행」. 점검 결과가 없거나 오류가 있거나 이 작업에 대한 점검이 아니면 아무것도 하지 않는다.
    /// 점검한 기간 그대로 실행한다.
    /// </summary>
    public async Task StartRunAsync(CancellationToken ct = default)
    {
        if (CurrentJob is null || IsBusy || Run.IsRunning || Precheck.Report is not { } report || !Precheck.CanRun
            || Step != DataCopyStep.Precheck || _checkedPeriod is not { } period)
        {
            return;
        }

        IsBusy = true;
        CopySessions? sessions;
        try
        {
            sessions = await PreflightAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (sessions is null)
        {
            return;
        }

        var decisions = Precheck.BuildDecisions();
        var job = CurrentJob;
        Step = DataCopyStep.Run;
        Say("복사하는 중…");
        await Run.StartAsync(sessions, job, report, decisions, period, ct).ConfigureAwait(true);
        if (Run.LastRun is { } last && CurrentJob is { } current && current.Id == job.Id)
        {
            // 점검한 작업과 같은 객체였다면(복사 도중 작업 내용이 바뀌지 않았다면) 마지막 실행 요약만 바뀐 새 객체를 점검한 작업으로도 맞춘다.
            // 그렇지 않으면 「실패 행만 다시 넣기」의 직전 확인이 같은 객체인지 비교해서 복사 직후에도 늘 거부한다.
            // 작업 내용이 실제로 바뀌었으면(다른 객체였으면) 점검한 작업은 그대로 두어 다시 점검을 요구한다.
            var unchangedSinceCheck = ReferenceEquals(_checkedJob, current);
            CurrentJob = current with { LastRun = last }; // 다음 저장이 옛 값으로 덮어쓰지 않게 열린 작업에 합친다
            if (unchangedSinceCheck)
            {
                _checkedJob = CurrentJob;
            }

            Editor.UpdateBase(CurrentJob);
        }

        Say(Run.StatusText, Run.Outcome is CopyOutcome.Failed);
        await Jobs.RefreshAsync(ct).ConfigureAwait(true);
    }

    /// <summary>열린 작업의 이름을 바꾼다(목록의 선택 항목이 열린 작업이면 편집 화면의 이름도 맞춘다).</summary>
    public async Task RenameSelectedAsync(string newName, CancellationToken ct = default)
    {
        var id = Jobs.Selected?.Job.Id;
        await Jobs.RenameAsync(newName, ct).ConfigureAwait(true);
        if (id is not null && CurrentJob?.Id == id)
        {
            CurrentJob = CurrentJob with { Name = newName };
            Editor.Name = newName;
            Editor.UpdateBase(CurrentJob);
        }
    }

    /// <summary>선택한 작업을 지운다. 열린 작업이면 화면을 비우고 세션도 닫는다(다음 점검이 지운 작업을 되살리지 않게).</summary>
    public async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        var id = Jobs.Selected?.Job.Id;
        if (id is not null && CurrentJob?.Id == id && RunInProgress())
        {
            return;
        }

        await Jobs.DeleteAsync(ct).ConfigureAwait(true);
        if (id is not null && CurrentJob?.Id == id)
        {
            await CloseSessionsAsync().ConfigureAwait(true);
            CurrentJob = null;
            Precheck.Reset();
            Run.Reset();
            UnfinishedRuns.Clear();
            Step = DataCopyStep.RuleEdit;
        }
    }

    /// <summary>창을 닫을 때나 다른 작업을 열 때: 도는 실행을 멈추고 끝나기를 기다린 뒤 두 세션을 닫는다.</summary>
    public async Task CloseSessionsAsync()
    {
        Run.Stop();
        Precheck.Cancel();
        if (Run.Completion is { } completion)
        {
            try
            {
                // 중지는 현재 배치를 되돌리고 끈 트리거·FK 를 다시 켠 뒤 끝난다. 그 정리가 끝나기 전에 세션을 닫지 않는다.
                await completion.WaitAsync(StopWait).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                ClosingWarning = "복사를 멈추는 데 너무 오래 걸려 세션을 닫았습니다. 대상 트리거·FK 상태를 확인하고, 다음에 이 작업을 열 때 「다시 켜기」를 쓰세요.";
                Say(ClosingWarning, error: true);
            }
        }

        await _sessionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await CloseSessionPairAsync().ConfigureAwait(true);
            await CloseMetadataSessionAsync().ConfigureAwait(true);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CloseSessionPairAsync()
    {
        var sessions = _sessions;
        _sessions = null;
        OnPropertyChanged(nameof(HasSessions));
        await DataCopySessions.CloseAsync(sessions).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await CloseSessionsAsync().ConfigureAwait(true);
    }
}
