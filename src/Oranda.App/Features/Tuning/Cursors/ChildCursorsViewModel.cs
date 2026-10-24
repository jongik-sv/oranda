using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.Tuning.Cursors;

/// <summary>비교 표 한 줄(계획 창 비교 줄 + 보일 Id 하나).</summary>
public sealed class ChildCompareRow(PlanCompareRowItem item)
{
    public PlanCompareRowItem Item { get; } = item;

    /// <summary>오른쪽 계획의 Id, 빠진 줄이면 왼쪽 Id.</summary>
    public string IdText => Item.CurrentIdText.Length > 0 ? Item.CurrentIdText : Item.PinnedIdText;
}

/// <summary>
/// <c>oranda.panel.childCursors</c> — sql_id 하나의 자식 커서(TN-04): 목록(바인드 민감·인식·공유 가능·plan_hash·실행당 값),
/// 불일치 사유(gv$sql_shared_cursor 의 Y 열), 체크한 두 자식의 계획 비교(TN-05, 계획 창의 <see cref="PlanComparer"/> 를 부르기만).
/// 조회는 SELECT 만(<see cref="ITuningService"/>). V$ 권한이 없으면 모니터와 같은 안내 띠를 켠다.
/// </summary>
[OrandaToolPanel(TuningCommands.ChildCursorsPanelId, ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true, Icon = "diff")]
public sealed partial class ChildCursorsViewModel : ToolPanelViewModelBase, IDisposable
{
    /// <summary>오른쪽 상태 줄의 출처 뷰.</summary>
    public const string SourceViews = "gv$sql · gv$sql_shared_cursor · gv$sql_plan";

    /// <summary>자식 계획을 읽을 DBMS_XPLAN 형식.</summary>
    public const string PlanFormat = "TYPICAL";

    private readonly IServiceProvider? _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChildCursorItem> _checkOrder = [];
    private readonly Dictionary<(int Child, int? Instance), CorePlan> _plans = [];
    private ITuningService? _tuning;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _compareCts;
    private ISession? _ownedSession;
    private bool _disposed;
    private bool _bulkChecking;

    [ObservableProperty]
    private string _sqlIdText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _sqlId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHint), nameof(ShowContent), nameof(HintIcon))]
    private TuningPanelState _state = TuningPanelState.NoSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(CanRefresh))]
    private bool _isBusy;

    [ObservableProperty]
    private string _hintTitle = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChildCountText), nameof(PlanHashCountText), nameof(PlanHashDiffers), nameof(BindAwareText), nameof(SummaryText),
        nameof(HasInstances), nameof(CanCompare), nameof(ReasonFooter), nameof(HasReasonFooter))]
    private IReadOnlyList<ChildCursorItem> _items = [];

    [ObservableProperty]
    private IReadOnlyList<ChildCursorItem> _visibleItems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _instances = [];

    [ObservableProperty]
    private int _selectedInstanceIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenPlan))]
    private ChildCursorItem? _currentItem;

    [ObservableProperty]
    private IReadOnlyList<CursorReasonItem> _reasons = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasonsHint))]
    private string _reasonsHint = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSqlText))]
    private string _sqlText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    private IReadOnlyList<ChildCompareRow> _compareRows = [];

    [ObservableProperty]
    private bool _compareDiffers;

    [ObservableProperty]
    private string _compareTitle = string.Empty;

    [ObservableProperty]
    private string _compareSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompareHint))]
    private string _compareHint = string.Empty;

    [ObservableProperty]
    private string _costHeader = T("비용", "Cost");

    [ObservableProperty]
    private string _rowsHeader = "E-Rows";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenStats))]
    private TableStatsTarget? _planTable;

    /// <summary>시험·디자이너용.</summary>
    public ChildCursorsViewModel()
    {
        SetHint(TuningPanelState.NoSession);
        CompareHint = T("비교할 두 자식을 표 첫 칸에서 체크하세요", "Check two children in the first column to compare their plans");
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스는 처음 쓸 때 꺼낸다.</summary>
    public ChildCursorsViewModel(IServiceProvider services)
        : this()
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>시험용: 서비스를 직접 준다.</summary>
    public ChildCursorsViewModel(ITuningService tuning)
        : this()
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public ISession? Session { get; private set; }

    /// <summary>권한 없음 안내 띠.</summary>
    public MonitorAccessState Access { get; } = new();

    /// <summary>SQL 전문을 읽는 방법(머리 줄). 없거나 실패하면 비운다.</summary>
    public Func<ISession, CursorRef, CancellationToken, Task<string?>>? SqlTextLoader { get; set; }

    /// <summary>계획 창에서 그 자식 계획을 여는 방법. 셸이 넣는다.</summary>
    public Func<ISession, CursorRef, Task>? OpenPlan { get; set; }

    /// <summary>통계 보기를 여는 방법. 셸이 넣는다.</summary>
    public Action<ISession, TableStatsTarget>? OpenStats { get; set; }

    /// <summary>「다른 접속으로 열기…」: 고른 프로필의 전용 세션. 취소하면 null. 이 화면이 닫을 때 닫는다.</summary>
    public Func<CancellationToken, Task<ISession?>>? ChooseSession { get; set; }

    /// <summary>처음 읽은 뒤 거를 인스턴스(RAC). 없거나 목록에 없으면 전체.</summary>
    public int? PreferredInstance { get; set; }

    /// <summary>세션 닫기를 기다리는 최대 시간. 시험이 줄인다.</summary>
    public TimeSpan? CloseTimeout { get; set; }

    public string Title => SqlId is null ? T("자식 커서", "Child Cursors") : T("자식 커서 — ", "Child cursors — ") + SqlId;

    public bool ShowHint => State is not TuningPanelState.Ready;

    public bool ShowContent => State is TuningPanelState.Ready;

    public string HintIcon => State switch
    {
        TuningPanelState.NoSession => "connect",
        TuningPanelState.Denied => "lock",
        TuningPanelState.Error => "warn",
        TuningPanelState.Loading => "refresh",
        TuningPanelState.Empty => "history",
        _ => "diff",
    };

    public bool CanRefresh => !IsBusy;

    /// <summary>두 자식이 체크돼 있고 자식이 둘 이상.</summary>
    public bool CanCompare => !IsBusy && Items.Count > 1 && _checkOrder.Count == 2;

    public bool CanOpenPlan => CurrentItem is not null && OpenPlan is not null;

    public bool CanOpenStats => PlanTable is not null && OpenStats is not null;

    public bool IsComparing => CompareRows.Count > 0;

    public bool HasCompareHint => CompareHint.Length > 0;

    public bool HasReasonsHint => ReasonsHint.Length > 0;

    public bool HasSqlText => SqlText.Length > 0;

    public bool HasInstances => Instances.Count > 2;

    public string ChildCountText => T("자식 ", "children ") + Items.Count.ToString(CultureInfo.InvariantCulture);

    public int PlanHashCount => Items.Select(i => i.Row.PlanHashValue).Distinct().Count();

    public string PlanHashCountText => "plan_hash " + PlanHashCount.ToString(CultureInfo.InvariantCulture);

    public bool PlanHashDiffers => PlanHashCount > 1;

    public string BindAwareText => T("바인드 인식 ", "bind aware ") + (Items.Any(i => i.Row.IsBindAware) ? T("예", "yes") : T("아니요", "no"));

    /// <summary>상태 줄 왼쪽 「자식 4 · 인스턴스 2 · plan_hash 2」.</summary>
    public string SummaryText
    {
        get
        {
            var instances = Items.Select(i => i.Row.InstanceId).Where(i => i is not null).Distinct().Count();
            var parts = new List<string> { ChildCountText };
            if (instances > 0)
            {
                parts.Add(T("인스턴스 ", "instances ") + instances.ToString(CultureInfo.InvariantCulture));
            }

            parts.Add(PlanHashCountText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>「child 0 은 공유 불가(N) — 새 실행은 1·2·3 으로 갑니다」. 모두 공유 가능하면 빈 글.</summary>
    public string ReasonFooter
    {
        get
        {
            var closed = Items.Where(i => !i.Row.IsShareable).Select(i => i.Row.ChildNumber).Distinct().ToList();
            var open = Items.Where(i => i.Row.IsShareable).Select(i => i.Row.ChildNumber).Distinct().ToList();
            if (closed.Count == 0)
            {
                return string.Empty;
            }

            var a = string.Join("·", closed);
            var b = string.Join("·", open);
            return open.Count == 0
                ? T($"child {a} 은 공유 불가(N) — 다음 실행은 새 자식을 만듭니다", $"child {a} not shareable (N) — the next run builds a new child")
                : T($"child {a} 은 공유 불가(N) — 새 실행은 {b} 으로 갑니다", $"child {a} not shareable (N) — new runs go to {b}");
        }
    }

    public bool HasReasonFooter => ReasonFooter.Length > 0;

    /// <summary>세션과 sql_id 를 정한다(읽지는 않는다).</summary>
    public void Attach(ISession? session, string? sqlId)
    {
        Session = session;
        SqlId = string.IsNullOrWhiteSpace(sqlId) ? null : sqlId.Trim();
        SqlIdText = SqlId ?? string.Empty;
        ClearResults();
        SetHint(session is null ? TuningPanelState.NoSession : SqlId is null ? TuningPanelState.NoTarget : TuningPanelState.Loading);
    }

    /// <summary>위 칸의 sql_id 로 읽는다.</summary>
    public Task ApplySqlIdTextAsync(CancellationToken ct = default)
    {
        var text = SqlIdText.Trim();
        if (!IsSqlId(text))
        {
            StatusText = T("sql_id 는 13자리 영숫자입니다.", "A sql_id is 13 letters and digits.");
            return Task.CompletedTask;
        }

        SqlId = text;
        return LoadAsync(ct);
    }

    /// <summary>sql_id 모양(영소문자·숫자 13자)인지. 대문자도 받는다.</summary>
    public static bool IsSqlId(string? text) =>
        text is { Length: 13 } && text.All(c => char.IsAsciiLetterOrDigit(c));

    /// <summary>자식 커서·불일치 사유를 읽는다. 자식이 둘 이상이고 plan_hash 가 다르면 서로 다른 계획의 두 자식을 체크해 비교한다.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (Session is not { } session || session.State is SessionState.Closed or SessionState.Broken)
        {
            SetHint(TuningPanelState.NoSession);
            return;
        }

        if (SqlId is not { } sqlId)
        {
            SetHint(TuningPanelState.NoTarget);
            return;
        }

        if (!session.Server.Capabilities.CanQueryPerformanceViews)
        {
            ShowDenied(session);
            return;
        }

        if (Service() is not { } tuning)
        {
            SetHint(TuningPanelState.Error, T("튜닝 조회가 준비되지 않았습니다", "Tuning queries are not available"),
                T("이 빌드에 자식 커서 조회 서비스(ITuningService)가 없습니다.", "This build has no child cursor service (ITuningService)."));
            return;
        }

        _loadCts?.Cancel();
        _compareCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCts = cts;
        var hadContent = State is TuningPanelState.Ready;
        IsBusy = true;
        if (!hadContent)
        {
            SetHint(TuningPanelState.Loading);
        }

        StatusText = T($"{sqlId} 자식 커서를 읽는 중…", $"Reading child cursors of {sqlId}…");
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            FinishCancelled(cts, hadContent);
            return;
        }

        IReadOnlyList<ChildCursorRow> rows;
        IReadOnlyList<SharedCursorReason>? reasons = null;
        string? reasonError = null;
        string? sqlText = null;
        try
        {
            rows = await tuning.GetChildCursorsAsync(session, sqlId, cts.Token).ConfigureAwait(true);
            if (rows.Count > 0)
            {
                try
                {
                    reasons = await tuning.GetSharedCursorReasonsAsync(session, sqlId, cts.Token).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    reasonError = FirstLine(ex.Message); // 사유를 못 읽어도 자식 목록은 보인다.
                }

                sqlText = await TrySqlTextAsync(session, rows[0], sqlId, cts.Token).ConfigureAwait(true);
            }

            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _gate.Release();
            FinishCancelled(cts, hadContent);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gate.Release();
            IsBusy = false;
            ShowFailure(ex, session);
            return;
        }

        _gate.Release();
        IsBusy = false;
        Access.ClearNotice();
        Apply(sqlId, rows, reasons, reasonError, sqlText);
        if (_checkOrder.Count == 2)
        {
            await CompareAsync(ct: cts.Token).ConfigureAwait(true);
        }
    }

    /// <summary>체크한 두 자식의 계획을 읽어 비교한다(<paramref name="reload"/> 면 읽어 둔 계획을 버리고 다시 읽는다).</summary>
    public async Task CompareAsync(bool reload = false, CancellationToken ct = default)
    {
        if (_checkOrder.Count != 2 || Session is not { } session || SqlId is null || Service() is not { } tuning)
        {
            ClearCompare();
            return;
        }

        var (left, right) = _checkOrder[0].Row.ChildNumber <= _checkOrder[1].Row.ChildNumber
            ? (_checkOrder[0], _checkOrder[1])
            : (_checkOrder[1], _checkOrder[0]);
        if (reload)
        {
            _plans.Remove(Key(left));
            _plans.Remove(Key(right));
        }

        _compareCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _compareCts = cts;
        CompareHint = T("두 자식의 계획을 읽는 중…", "Reading both child plans…");
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        CorePlan a;
        CorePlan b;
        try
        {
            a = await PlanAsync(tuning, session, left, cts.Token).ConfigureAwait(true);
            b = await PlanAsync(tuning, session, right, cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CompareRows = [];
            CompareHint = T("계획을 읽지 못했습니다: ", "Could not read the plans: ") + FirstLine(ex.Message);
            return;
        }
        finally
        {
            _gate.Release();
        }

        var comparison = PlanComparer.Compare(a, b);
        CompareRows = [.. comparison.Rows.Select(r => new ChildCompareRow(new PlanCompareRowItem(r)))];
        var changed = comparison.Count(PlanDiffKind.Changed);
        var added = comparison.Count(PlanDiffKind.Added);
        var removed = comparison.Count(PlanDiffKind.Removed);
        CompareDiffers = left.Row.PlanHashValue != right.Row.PlanHashValue || changed + added + removed > 0;
        CompareTitle = CompareDiffers ? T("두 계획이 다릅니다", "The two plans differ") : T("두 계획이 같습니다", "The two plans are the same");
        CompareSummary = CompareDiffers
            ? string.Create(CultureInfo.InvariantCulture, $"{left.Label} ↔ {right.Label} · {T("바뀜", "changed")} {changed} · {T("추가", "added")} {added} · {T("빠짐", "removed")} {removed}")
            : string.Create(CultureInfo.InvariantCulture, $"{left.Label} ↔ {right.Label}");
        CostHeader = string.Create(CultureInfo.InvariantCulture, $"{T("비용", "Cost")} {left.Row.ChildNumber} → {right.Row.ChildNumber}");
        RowsHeader = string.Create(CultureInfo.InvariantCulture, $"E-Rows {left.Row.ChildNumber} → {right.Row.ChildNumber}");
        CompareHint = string.Empty;
        PlanTable = FirstTable(a) ?? FirstTable(b);
    }

    /// <summary>「실제 계획」: 지금 행의 자식 계획을 계획 창에서 연다.</summary>
    public Task OpenCurrentPlanAsync()
    {
        if (CurrentItem is not { } item || Session is not { } session || OpenPlan is null)
        {
            return Task.CompletedTask;
        }

        return OpenPlan(session, item.Cursor);
    }

    /// <summary>「통계 보기」: 비교한 계획의 첫 표 통계를 연다.</summary>
    public void OpenPlanTableStats()
    {
        if (PlanTable is { } table && Session is { } session)
        {
            OpenStats?.Invoke(session, table);
        }
    }

    /// <summary>「다른 접속으로 열기…」: 고른 프로필의 세션으로 다시 읽는다(앞서 이 화면이 연 세션은 닫는다).</summary>
    public async Task OpenWithOtherAccountAsync(CancellationToken ct = default)
    {
        if (ChooseSession is null)
        {
            return;
        }

        ISession? chosen;
        try
        {
            chosen = await ChooseSession(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            StatusText = T("세션을 열지 못했습니다: ", "Could not open the session: ") + FirstLine(ex.Message);
            return;
        }

        if (chosen is null)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _ownedSession, chosen);
        Session = chosen;
        if (previous is not null)
        {
            await ConnectionService.CloseAsync(previous, timeout: CloseTimeout).ConfigureAwait(true);
        }

        await LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>「복사」: 자식 커서 표와 불일치 사유를 탭으로 나눈 글로.</summary>
    public string BuildCopyText()
    {
        if (SqlId is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("sql_id ").Append(SqlId).Append('\n');
        sb.AppendJoin('\t', ChildCursorItem.Headers.Skip(1)).Append('\n');
        foreach (var i in Items)
        {
            sb.AppendJoin('\t', i.Cells.Skip(1)).Append('\n');
        }

        sb.Append('\n').AppendJoin('\t', CursorReasonItem.Headers).Append('\n');
        foreach (var r in Reasons)
        {
            sb.AppendJoin('\t', r.Cells).Append('\n');
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts?.Cancel();
        _compareCts?.Cancel();
        foreach (var i in Items)
        {
            i.PropertyChanged -= OnItemChanged;
        }

        if (Interlocked.Exchange(ref _ownedSession, null) is { } owned)
        {
            _ = ConnectionService.CloseAsync(owned, timeout: CloseTimeout);
        }
    }

    partial void OnSelectedInstanceIndexChanged(int value) => ApplyInstanceFilter();

    private void Apply(string sqlId, IReadOnlyList<ChildCursorRow> rows, IReadOnlyList<SharedCursorReason>? reasons, string? reasonError, string? sqlText)
    {
        foreach (var i in Items)
        {
            i.PropertyChanged -= OnItemChanged;
        }

        _checkOrder.Clear();
        _plans.Clear();
        ClearCompare();
        SqlText = sqlText ?? string.Empty;
        if (rows.Count == 0)
        {
            Items = [];
            VisibleItems = [];
            Reasons = [];
            Instances = [];
            SetHint(TuningPanelState.Empty, T("공유 풀에서 사라졌습니다", "The cursor is no longer in the shared pool"),
                T($"{sqlId} 의 커서가 공유 풀에 없습니다(aged out). 문장을 다시 실행한 뒤 보세요.", $"No cursor for {sqlId} is in the shared pool (aged out). Run the statement again, then look."));
            StatusText = T("자식 커서 0건", "0 child cursors");
            return;
        }

        var items = rows
            .OrderBy(r => r.InstanceId ?? 0)
            .ThenBy(r => r.ChildNumber)
            .Select(r => new ChildCursorItem(r, sqlId))
            .ToList();
        ChildCursorItem.MarkWorse(items);
        Items = items;
        foreach (var i in items)
        {
            i.PropertyChanged += OnItemChanged;
        }

        var ids = items.Select(i => i.Row.InstanceId).Where(i => i is not null).Distinct().OrderBy(i => i).ToList();
        Instances = [T("인스턴스: 전체", "Instance: all"), .. ids.Select(i => T("인스턴스 ", "Instance ") + i!.Value.ToString(CultureInfo.InvariantCulture))];
        var preferred = PreferredInstance is { } want ? ids.IndexOf(want) : -1;
        SelectedInstanceIndex = preferred >= 0 ? preferred + 1 : 0;
        ApplyInstanceFilter();

        Reasons = BuildReasons(reasons ?? []);
        ReasonsHint = reasonError is not null
            ? T("불일치 사유를 읽지 못했습니다: ", "Could not read the mismatch reasons: ") + reasonError
            : Reasons.Count == 0 ? T("불일치 없음", "No mismatch") : string.Empty;
        CurrentItem = items[0];
        State = TuningPanelState.Ready;

        // 처음 체크: plan_hash 가 다른 첫 두 자식(같으면 처음 두 자식). 자식이 하나면 비교 없음.
        if (items.Count > 1)
        {
            var first = items[0];
            var second = items.FirstOrDefault(i => i.Row.PlanHashValue != first.Row.PlanHashValue) ?? items[1];
            _bulkChecking = true;
            first.IsCompared = true;
            second.IsCompared = true;
            _bulkChecking = false;
            _checkOrder.AddRange([first, second]);
        }
        else
        {
            CompareHint = T("자식이 하나라 비교할 계획이 없습니다", "There is only one child, so there is nothing to compare");
        }

        OnPropertyChanged(nameof(CanCompare));
        StatusText = SummaryText;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChildCursorItem.IsCompared) || sender is not ChildCursorItem item || _bulkChecking)
        {
            return;
        }

        if (item.IsCompared)
        {
            _checkOrder.Remove(item);
            _checkOrder.Add(item);
            if (_checkOrder.Count > 2)
            {
                var oldest = _checkOrder[0];
                _checkOrder.RemoveAt(0);
                _bulkChecking = true;
                oldest.IsCompared = false;
                _bulkChecking = false;
            }
        }
        else
        {
            _checkOrder.Remove(item);
        }

        OnPropertyChanged(nameof(CanCompare));
        if (_checkOrder.Count == 2)
        {
            _ = CompareAsync(ct: _loadCts?.Token ?? default); // 체크 두 개가 되면 비교를 다시 그린다.
        }
        else
        {
            _compareCts?.Cancel();
            ClearCompare();
        }
    }

    private static List<CursorReasonItem> BuildReasons(IReadOnlyList<SharedCursorReason> reasons)
    {
        var list = new List<CursorReasonItem>();
        string? previous = null;
        foreach (var r in reasons.OrderBy(r => r.ChildNumber).ThenBy(r => r.Reason, StringComparer.Ordinal))
        {
            var meaning = CursorMismatchReasons.Meaning(r.Reason) ?? DetailFirstLine(r.Detail);
            var shown = previous is not null && string.Equals(previous, r.Reason, StringComparison.Ordinal) && meaning is not null
                ? T("위와 같음", "Same as above")
                : meaning;
            list.Add(new CursorReasonItem(r, shown));
            previous = r.Reason;
        }

        return list;
    }

    private static string? DetailFirstLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var line = FirstLine(detail);
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private void ApplyInstanceFilter()
    {
        var ids = Items.Select(i => i.Row.InstanceId).Where(i => i is not null).Distinct().OrderBy(i => i).ToList();
        var index = SelectedInstanceIndex;
        VisibleItems = index <= 0 || index > ids.Count
            ? Items
            : [.. Items.Where(i => i.Row.InstanceId == ids[index - 1])];
    }

    private async Task<CorePlan> PlanAsync(ITuningService tuning, ISession session, ChildCursorItem item, CancellationToken ct)
    {
        if (_plans.TryGetValue(Key(item), out var cached))
        {
            return cached;
        }

        var plan = await tuning.GetChildPlanAsync(session, item.Cursor, PlanFormat, ct).ConfigureAwait(true);
        _plans[Key(item)] = plan;
        return plan;
    }

    private static (int, int?) Key(ChildCursorItem item) => (item.Row.ChildNumber, item.Row.InstanceId);

    // 계획의 첫 TABLE ACCESS 객체(통계 보기 대상).
    private static TableStatsTarget? FirstTable(CorePlan plan) =>
        plan.Steps.FirstOrDefault(s => s.Operation.StartsWith("TABLE ACCESS", StringComparison.OrdinalIgnoreCase) && s.ObjectName is not null) is { } step
            ? new TableStatsTarget(step.ObjectOwner ?? string.Empty, step.ObjectName!) is { Owner.Length: > 0 } t ? t : null
            : null;

    private async Task<string?> TrySqlTextAsync(ISession session, ChildCursorRow row, string sqlId, CancellationToken ct)
    {
        if (SqlTextLoader is null)
        {
            return null;
        }

        try
        {
            return await SqlTextLoader(session, new CursorRef(sqlId, row.ChildNumber, row.InstanceId), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // 머리 줄 전문은 덤.
        }
    }

    private ITuningService? Service() => _tuning ??= _services?.GetService(typeof(ITuningService)) as ITuningService;

    private void ClearResults()
    {
        foreach (var i in Items)
        {
            i.PropertyChanged -= OnItemChanged;
        }

        _checkOrder.Clear();
        _plans.Clear();
        Items = [];
        VisibleItems = [];
        Reasons = [];
        Instances = [];
        SqlText = string.Empty;
        CurrentItem = null;
        ClearCompare();
    }

    private void ClearCompare()
    {
        CompareRows = [];
        CompareDiffers = false;
        CompareTitle = string.Empty;
        CompareSummary = string.Empty;
        PlanTable = null;
        CompareHint = Items.Count == 1
            ? T("자식이 하나라 비교할 계획이 없습니다", "There is only one child, so there is nothing to compare")
            : T("비교할 두 자식을 표 첫 칸에서 체크하세요", "Check two children in the first column to compare their plans");
    }

    private void FinishCancelled(CancellationTokenSource cts, bool hadContent)
    {
        if (!ReferenceEquals(_loadCts, cts))
        {
            return;
        }

        IsBusy = false;
        if (!hadContent)
        {
            SetHint(TuningPanelState.NoTarget, T("읽기를 취소했습니다", "Reading was cancelled"), T("F5 로 다시 읽습니다.", "Press F5 to read again."));
        }

        StatusText = T("취소했습니다.", "Cancelled.");
    }

    private void ShowDenied(ISession session)
    {
        ClearResults();
        Access.ShowDenied(session.Profile);
        SetHint(TuningPanelState.Denied, T("V$ 뷰를 읽을 권한이 없습니다", "No privilege to read the V$ views"),
            T($"{session.Profile.User} 계정으로는 gv$sql·gv$sql_shared_cursor 를 읽을 수 없습니다. 위 띠에서 다른 접속으로 열거나 권한을 받으세요.",
              $"The {session.Profile.User} account cannot read gv$sql / gv$sql_shared_cursor. Open with another connection from the strip above, or get the privilege."));
        StatusText = HintTitle;
    }

    private void ShowFailure(Exception ex, ISession session)
    {
        if (MonitorAccessState.IsPrivilegeError(ex))
        {
            ShowDenied(session);
            return;
        }

        ClearResults();
        if (ex is NotSupportedException or NotImplementedException)
        {
            SetHint(TuningPanelState.Error, T("튜닝 조회가 준비되지 않았습니다", "Tuning queries are not available"),
                T("이 접속의 자식 커서 조회가 아직 구현되지 않았습니다.", "Child cursor queries are not implemented for this connection yet."));
        }
        else
        {
            SetHint(TuningPanelState.Error, T("자식 커서를 읽지 못했습니다", "Could not read the child cursors"), FirstLine(ex.Message));
        }

        StatusText = FirstLine(ex.Message);
    }

    private void SetHint(TuningPanelState state, string? title = null, string? text = null)
    {
        State = state;
        (HintTitle, HintText) = state switch
        {
            TuningPanelState.NoSession => (T("접속 없음", "No connection"), T("DB 에 접속한 뒤 다시 여세요.", "Connect to a database and open this again.")),
            TuningPanelState.NoTarget => (T("sql_id 를 넣으세요", "Enter a sql_id"),
                T("위 칸에 sql_id 를 넣고 Enter, 또는 SQL 모니터·계획 창에서 「자식 커서」를 누르세요.",
                  "Type a sql_id above and press Enter, or press Child cursors in the SQL monitor or plan window.")),
            TuningPanelState.Loading => (T("자식 커서를 읽는 중…", "Reading child cursors…"), SqlId ?? string.Empty),
            _ => (string.Empty, string.Empty),
        };
        if (title is not null)
        {
            HintTitle = title;
        }

        if (text is not null)
        {
            HintText = text;
        }
    }
}
