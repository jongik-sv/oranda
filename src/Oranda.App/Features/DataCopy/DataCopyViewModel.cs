using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
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

    // 이 창이 연 세션마다 열 때 넘긴 저장 프로필(SameConnection 의 비교 기준). 세션이 닫혀 버려지면 함께 사라진다.
    private readonly ConditionalWeakTable<ISession, ConnectionProfile> _openedWith = new();

    private bool _disposed;
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

    /// <summary>
    /// 열린 작업이 이 앱보다 새 버전 파일이라 읽기 전용으로 열렸는지. 모르는 필드(표마다 계정 등)를 버린 채 실행하면 다른 계정의 표를 지울 수 있으므로
    /// 저장뿐 아니라 사전 점검·실행도 막는다(묶음 B 설계 2장).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoToPrecheck), nameof(CanStartRun), nameof(ReadOnlyJobNotice), nameof(HasReadOnlyJobNotice))]
    private bool _isReadOnlyJob;

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

            if (e.PropertyName == nameof(PrecheckViewModel.Report))
            {
                OnPropertyChanged(nameof(HasPrecheckResult));
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

    /// <summary>점검을 시작할 수 있는지: 열린 작업이 있고, 다른 점검·세션 열기·실행이 도는 중이 아니며, 대상이 읽기 전용이 아니고, 새 버전 작업이 아닐 때.</summary>
    public bool CanGoToPrecheck => HasJob && !IsBusy && !Run.IsRunning && !Editor.HasTargetReadOnlyNotice && !Editor.HasUnsupportedPairNotice && !IsReadOnlyJob;

    /// <summary>
    /// 규칙 편집에서 「점검 결과 보기」를 보일지: 보일 점검 결과가 있고(규칙 편집으로 돌아와도 결과를 버리지 않는다) 그 결과로 아직 실행하지 않았을 때.
    /// </summary>
    public bool HasPrecheckResult => Precheck.Report is not null && !_checkUsedByRun;

    // 마지막 점검 결과로 복사를 실행했다: 대상이 바뀌었을 수 있어(만든 테이블·더한 열) 그 결과를 다시 쓰지 않는다(다시 점검한다).
    private bool _checkUsedByRun;

    /// <summary>「복사 실행」: 점검이 통과했고 다른 점검·세션 열기·실행이 도는 중이 아니며, 새 버전 작업이 아닐 때.</summary>
    public bool CanStartRun => Precheck.CanRun && !IsBusy && !Run.IsRunning && !IsReadOnlyJob;

    /// <summary>새 버전 작업이면 점검·실행을 막은 이유. 아니면 빈 글자.</summary>
    public string ReadOnlyJobNotice => IsReadOnlyJob ? DataCopyText.L(DataCopyText.NewerVersionJob) : string.Empty;

    public bool HasReadOnlyJobNotice => IsReadOnlyJob;

    // 새 버전 작업이면 상태 줄에 이유를 남기고 참.
    private bool ReadOnlyJobBlocked()
    {
        if (!IsReadOnlyJob)
        {
            return false;
        }

        Say(DataCopyText.NewerVersionJob, error: true);
        return true;
    }

    public bool HasUnfinished => UnfinishedRuns.Count > 0;

    /// <summary>
    /// 실행 확인 대화상자(창이 복사 창을 소유자로 띄워 연결한다). 물어야 하는 작업(<see cref="RunConfirmation.For"/>)인데 연결이 없으면
    /// 묻지 못했으니 실행하지 않는다(fail-closed).
    /// </summary>
    public Func<RunConfirmation, Task<bool>>? ConfirmRun { get; set; }

    /// <summary>
    /// 글 한 줄을 보이고 예/아니오를 묻는 대화상자(창이 복사 창을 소유자로 띄워 연결한다). 규칙을 여럿 지울 때 쓴다.
    /// 연결이 없으면 묻지 못했으니 지우지 않는다(fail-closed).
    /// </summary>
    public Func<string, Task<bool>>? ConfirmMessage { get; set; }

    /// <summary>
    /// 고른 규칙을 지운다. 둘 이상이면 확인 창을 한 번 띄우고(취소하면 아무것도 지우지 않는다), 하나면 묻지 않고 지운다.
    /// </summary>
    /// <returns>지웠으면 true.</returns>
    public async Task<bool> RemoveRulesAsync(IReadOnlyList<TableRuleViewModel> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);
        var rules = picked.Distinct().Where(Editor.Rules.Contains).ToList();
        if (rules.Count == 0)
        {
            return false;
        }

        if (rules.Count > 1)
        {
            if (ConfirmMessage is not { } ask)
            {
                Say(DataCopyText.L("확인 창을 띄울 수 없어 규칙을 지우지 않았습니다."), error: true);
                return false;
            }

            var message = DataCopyText.L("{0}개 테이블 규칙을 지웁니다. 계속할까요?")
                .Replace("{0}", rules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!await ask(message).ConfigureAwait(true))
            {
                return false;
            }
        }

        return Editor.RemoveRules(rules) > 0;
    }

    public bool IsRuleEdit => Step == DataCopyStep.RuleEdit;

    public bool IsPrecheck => Step == DataCopyStep.Precheck;

    public bool IsRun => Step == DataCopyStep.Run;

    /// <summary>원본·대상 세션이 열려 있는지.</summary>
    public bool HasSessions => _sessions is not null;

    // 이미 세션이 열려 있으면 규칙을 고를 때 열 정보를 자동으로 읽는다. 열려 있지 않으면 비밀번호를 묻게 되므로 「열 정보 읽기」를 눌렀을 때만 읽는다.
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RuleEditorViewModel.HasTargetReadOnlyNotice) or nameof(RuleEditorViewModel.HasUnsupportedPairNotice))
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

    /// <summary>변수 줄 「조건에 넣기」: 고른 규칙의 WHERE 에 변수 조건을 넣고 결과를 상태 줄에 알린다.</summary>
    public void InsertVariableCondition(CopyVariableViewModel variable)
    {
        var result = Editor.InsertVariableCondition(variable);
        Say(result.Message, result.NeedsAttention);
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

        // 다른 작업을 열기 전에 지금 작업에서 한 일을 저장한다(사전 점검으로 넘어가지 않았어도 잃지 않게).
        if (CurrentJob is { } open && open.Id != job.Id)
        {
            await SaveCurrentJobAsync(quiet: true, ct).ConfigureAwait(true);
        }

        await RefreshProfilesAsync(ct).ConfigureAwait(true);
        await CloseSessionsAsync().ConfigureAwait(true);
        Precheck.Reset();
        Run.Reset();
        _checkedPeriod = null;
        _checkUsedByRun = false;
        IsReadOnlyJob = IsReadOnlyEntry(job);
        Run.SaveLastRun = !IsReadOnlyJob;
        var source = ConnectionRelinker.Resolve(job.Source, _profiles, isTarget: false);
        var target = ConnectionRelinker.Resolve(job.Target, _profiles, isTarget: true);
        var notices = new List<string>();
        if (IsReadOnlyJob)
        {
            notices.Add(DataCopyText.NewerVersionJob);
        }

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

    /// <summary>
    /// 연 작업이 새 버전 파일(읽기 전용)인지: ID 가 아니라 고른 목록 항목으로 본다(같은 ID 의 파일이 둘이면 ID 로는 다른 파일의 값을 볼 수 있다).
    /// 고른 항목이 이 작업이면 그 항목, 아니면 같은 작업 객체의 항목을 본다. 둘 다 없으면 같은 ID 의 항목 가운데 하나라도 읽기 전용이면 읽기 전용으로 본다(막는 쪽).
    /// </summary>
    private bool IsReadOnlyEntry(CopyJob job)
    {
        if (Jobs.Selected is { } selected && ReferenceEquals(selected.Job, job))
        {
            return selected.IsReadOnly;
        }

        if (Jobs.Items.FirstOrDefault(i => ReferenceEquals(i.Job, job)) is { } item)
        {
            return item.IsReadOnly;
        }

        return Jobs.Items.Any(i => i.Job.Id == job.Id && i.IsReadOnly);
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
            var session = reuse ?? await OpenSessionAsync(target, ct).ConfigureAwait(true);
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
        var owner = SqlIdentifier.QuoteAlways(disabled.Owner);
        if (disabled.Kind == DisabledKind.Trigger)
        {
            return await _services.RunStatementAsync(session, $"ALTER TRIGGER {owner}.{SqlIdentifier.QuoteAlways(disabled.Name)} ENABLE", ct).ConfigureAwait(true);
        }

        var alter = $"ALTER TABLE {owner}.{SqlIdentifier.QuoteAlways(disabled.Table)}";
        var error = await _services.RunStatementAsync(session, $"{alter} ENABLE VALIDATE CONSTRAINT {SqlIdentifier.QuoteAlways(disabled.Name)}", ct).ConfigureAwait(true);
        return error is null
            ? null
            : await _services.RunStatementAsync(session, $"{alter} ENABLE NOVALIDATE CONSTRAINT {SqlIdentifier.QuoteAlways(disabled.Name)}", ct).ConfigureAwait(true);
    }

    private static bool SameJobContent(CopyJob a, CopyJob b) =>
        string.Equals(
            System.Text.Json.JsonSerializer.Serialize(a, CopyJobJsonContext.Default.CopyJob),
            System.Text.Json.JsonSerializer.Serialize(b, CopyJobJsonContext.Default.CopyJob),
            StringComparison.Ordinal);

    /// <summary>
    /// 규칙 편집 중인 작업을 그대로 저장한다(입력 확인 없이 — 사전 점검으로 넘어가지 않아도 한 일을 잃지 않게).
    /// 바뀐 것이 없으면 쓰지 않는다. 「저장」 단추·Ctrl+S, 다른 작업을 열 때, 창을 닫을 때 부른다.
    /// </summary>
    /// <param name="quiet">참이면 저장했다는 알림을 띄우지 않는다(작업을 바꾸거나 창을 닫을 때). 실패는 늘 알린다.</param>
    /// <returns>저장했으면 true.</returns>
    public async Task<bool> SaveCurrentJobAsync(bool quiet = false, CancellationToken ct = default)
    {
        if (CurrentJob is not { } saved || _services.Store is not { } store)
        {
            return false;
        }

        if (IsReadOnlyJob)
        {
            if (!quiet)
            {
                Say(DataCopyText.NewerVersionJob, error: true);
            }

            return false;
        }

        CopyJob job;
        try
        {
            job = Editor.BuildJob();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (SameJobContent(job, saved))
        {
            if (!quiet)
            {
                Say("바뀐 내용이 없습니다(이미 저장됨).");
            }

            return false;
        }

        try
        {
            await store.SaveAsync(job, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Say("작업을 저장하지 못했습니다: " + e.Message, error: true);
            return false;
        }

        CurrentJob = job;
        Editor.UpdateBase(job);
        if (!quiet)
        {
            Say($"작업 「{job.Name}」 을(를) 저장했습니다.");
        }

        await Jobs.RefreshAsync(ct).ConfigureAwait(true);
        return true;
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

        // 규칙마다 원본 계정이 다를 수 있다(시노님으로 더한 다른 계정의 표).
        var owner = Editor.EffectiveSource(rule) is { Length: > 0 } ruleOwner ? ruleOwner : OpenedWith(session).User.ToUpperInvariant();
        await Editor.LoadColumnsAsync(rule, new DbObjectRef(owner, rule.Table, "TABLE"), session, ct).ConfigureAwait(true);
        Say(Editor.Notice.Length > 0 ? Editor.Notice : $"{owner}.{rule.Table} 의 열 정보를 읽었습니다.", Editor.Notice.Length > 0);
    }

    /// <summary>
    /// 점검 선택지 중 규칙을 바꾸는 것을 적용하고 점검을 다시 한다. 점검 결과를 먼저 지운 뒤 작업을 바꾸고 저장한다
    /// (저장이 실패하거나 오래 걸려도 옛 점검 결과의 문장으로 바뀐 작업을 실행하지 않게).
    /// </summary>
    public async Task ApplyChoiceAsync(CheckItemViewModel item, CheckChoice choice, CancellationToken ct = default)
    {
        if (CurrentJob is null || _sessions is null || IsBusy || Precheck.IsRunning || Precheck.IsStale || Run.IsRunning || IsReadOnlyJob
            || PrecheckViewModel.ApplyJobChoice(CurrentJob, item, choice) is not { } changed)
        {
            return;
        }

        await ApplyJobChangeAsync(changed, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 「선택한 것 처리」(PC1, 결정 D-066): 고른 처리 가운데 실행 결정(트리거·자식 FK 끄기 등)은 그 선택지를 고르고, 규칙을 바꾸는 처리는 작업 규칙 값
    /// (테이블별로 따로 바꾼 값)을 바꿔 저장한 뒤 사전 점검을 다시 한다. 끝나면 한 줄 요약을 남긴다(「3건 처리 — 사전 점검을 다시 했습니다」).
    /// 점검은 대상에 DDL 을 보내지 않는다 — 표 만들기·열 추가는 실행할 때 미리 보기의 문장 그대로 보낸다. 바꾼 규칙은 규칙 편집에서 되돌릴 수 있다.
    /// 결과가 옛것이면(규칙을 고친 뒤) 하지 않는다: 지금 편집 중인 규칙을 점검한 작업으로 덮어쓰게 된다.
    /// </summary>
    public async Task ApplySelectedFixesAsync(CancellationToken ct = default)
    {
        var picked = Precheck.SelectedFixes();
        if (picked.Count == 0 || CurrentJob is not { } job || _sessions is null || IsBusy || Precheck.IsRunning || Precheck.IsStale || Run.IsRunning || IsReadOnlyJob
            || Precheck.Report is null)
        {
            return;
        }

        Precheck.ChooseDecisions(picked.Where(f => !f.ChangesJob));
        var changed = CopyPrecheckFixes.Apply(job, picked);
        string summary;
        if (ReferenceEquals(changed, job))
        {
            Precheck.ClearFixSelection();
            summary = DataCopyText.FixesApplied(picked.Count, rechecked: false);
        }
        else
        {
            await ApplyJobChangeAsync(changed, ct).ConfigureAwait(true);
            if (Precheck.Report is null)
            {
                return; // 저장·점검을 마치지 못했다(이유는 상태 줄에 있다)
            }

            summary = DataCopyText.FixesApplied(picked.Count, rechecked: true);
        }

        Precheck.FixSummary = summary;
        Say(summary);
    }

    /// <summary>
    /// 「연결」 체크: 시퀀스 규칙을 작업에 넣거나 빼고 저장한 뒤 점검을 다시 한다(연결한 규칙은 점검의 시퀀스 점검 대상이 된다).
    /// 지금 바꿀 수 없는 상태(점검·실행이 도는 중, 세션 없음)면 체크를 원래대로 돌린다.
    /// </summary>
    public async Task SetSequenceLinkAsync(SequenceLinkViewModel link, bool linked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (CurrentJob is null || _sessions is null || IsBusy || Precheck.IsRunning || Precheck.IsStale || Run.IsRunning || IsReadOnlyJob
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
            if (_services.Store is { } store && !IsReadOnlyJob)
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
            _checkUsedByRun = false;
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
            IsReadOnlyJob = false;
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
        _services.ForgetSecrets();
    }
}
