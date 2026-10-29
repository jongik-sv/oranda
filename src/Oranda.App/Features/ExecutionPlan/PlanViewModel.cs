using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 한 줄(PLAN-01·03). 위험 단계는 <see cref="RiskText"/> 로, 가장 비싼 단계는 <see cref="IsHottest"/> 로 강조한다.
/// 화면 글·막대 폭은 만들 때 한 번만 구한다(가상화된 목록이 줄을 다시 묶어도 새로 만들지 않는다).
/// </summary>
public sealed class PlanRowItem : ObservableObject
{
    /// <summary>비중 막대의 가장 긴 폭(DIP). PlanView.axaml 의 막대 칸과 맞춘다.</summary>
    public const double BarWidth = 60;

    public PlanRowItem(PlanStep step)
        : this(step, default, PlanActualMetric.None, hottest: false)
    {
    }

    public PlanRowItem(PlanStep step, PlanStepMetrics metrics, PlanActualMetric actual, bool hottest, PlanTreeLines? lines = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        Step = step;
        OperationText = OperationName(step);
        Operation = new string(' ', step.Depth * 2) + OperationText;
        IconKey = PlanOperationIcons.KeyFor(step);
        _lines = lines ?? new PlanTreeLines(step.Depth, true, false, new bool[Math.Max(0, step.Depth - 1)]);
        _collapsedLines = _lines with { HasChildren = false };
        HasActuals = actual != PlanActualMetric.None;
        AccessPredicates = string.IsNullOrEmpty(step.AccessPredicates) ? null : step.AccessPredicates;
        FilterPredicates = string.IsNullOrEmpty(step.FilterPredicates) ? null : step.FilterPredicates;
        ObjectText = ObjectName(step);
        CostText = Fmt(step.Cost);
        EstimatedText = Fmt(step.EstimatedRows);
        ActualText = Fmt(step.ActualRows);
        StartsText = Fmt(step.Starts);
        TimeText = step.ActualTime is { } t ? Seconds(t) : string.Empty;
        Predicates = string.Join(" | ", new[]
        {
            step.AccessPredicates is { Length: > 0 } a ? "access: " + a : null,
            step.FilterPredicates is { Length: > 0 } f ? "filter: " + f : null,
        }.Where(x => x is not null));
        RiskText = RiskDescription(step.Risk);
        ExecutionOrderText = metrics.ExecutionOrder > 0 ? metrics.ExecutionOrder.ToString(CultureInfo.InvariantCulture) : string.Empty;

        if (step.Cost is { } cost)
        {
            CostBarWidth = Bar(metrics.CostShare);
            SelfCostBarWidth = Bar(metrics.SelfCostShare);
            CostShareText = Percent(metrics.CostShare);
            CostTip = $"누적 비용 {Fmt(cost)} (전체의 {Percent(metrics.CostShare)}) · 자체 비용 {Fmt(metrics.SelfCost)} ({Percent(metrics.SelfCostShare)})";
        }

        switch (actual)
        {
            case PlanActualMetric.Time when step.ActualTime is { } time:
                ActualBarWidth = Bar(metrics.ActualShare);
                SelfActualBarWidth = Bar(metrics.SelfActualShare);
                ActualShareText = Percent(metrics.ActualShare);
                ActualTip = $"누적 시간 {Seconds(time)}s (전체의 {Percent(metrics.ActualShare)}) · 자체 시간 {Seconds(metrics.SelfTime ?? TimeSpan.Zero)}s ({Percent(metrics.SelfActualShare)})";
                break;
            case PlanActualMetric.Rows when step.ActualRows is { } rows:
                ActualBarWidth = Bar(metrics.ActualShare);
                SelfActualBarWidth = ActualBarWidth;
                ActualShareText = Percent(metrics.ActualShare);
                ActualTip = $"A-Rows {Fmt(rows)} (가장 많은 단계의 {Percent(metrics.ActualShare)}, 시간 통계 없음)";
                break;
        }

        GraphTitle = step.Id.ToString(CultureInfo.InvariantCulture) + " " + OperationText;
        CostDetailText = CostText.Length == 0 ? string.Empty : CostShareText.Length == 0 ? CostText : $"{CostText} ({CostShareText})";
        TimeDetailText = TimeText.Length == 0 ? string.Empty : ActualShareText.Length == 0 ? TimeText + " s" : $"{TimeText} s ({ActualShareText})";
        IsHottest = hottest;
        if (hottest)
        {
            HotTip = actual == PlanActualMetric.Time ? "가장 비싼 단계(자체 시간 기준)" : "가장 비싼 단계(자체 비용 기준)";
        }
    }

    public PlanStep Step { get; }

    /// <summary>그래프 상세 머리(「4 TABLE ACCESS FULL」).</summary>
    public string GraphTitle { get; }

    /// <summary>「8,380 (99%)」 — 누적 비용과 비중.</summary>
    public string CostDetailText { get; }

    /// <summary>「1.600 s (87%)」 — 누적 A-Time 과 비중.</summary>
    public string TimeDetailText { get; }

    public int Id => Step.Id;

    /// <summary>실행 순서(자식 먼저, 형제는 Id 순).</summary>
    public string ExecutionOrderText { get; }

    /// <summary>깊이만큼 들여쓴 연산 이름(글 표현용). 화면 트리는 <see cref="OperationText"/> 와 <see cref="TreeLines"/> 로 그린다.</summary>
    public string Operation { get; }

    /// <summary>들여쓰지 않은 연산 이름(예: <c>TABLE ACCESS FULL</c>).</summary>
    public string OperationText { get; }

    public int Depth => Step.Depth;

    /// <summary>연산 종류 아이콘 키(<see cref="PlanOperationIcons"/>).</summary>
    public string IconKey { get; }

    private readonly PlanTreeLines _lines;
    private readonly PlanTreeLines _collapsedLines;
    private bool _isCollapsed;
    private int _hiddenCount;
    private bool _hiddenHottest;

    /// <summary>들여쓰기 안내선(├ └ │). 접힌 줄은 아래로 내리는 선을 뺀다(보이는 자식이 없다).</summary>
    public PlanTreeLines TreeLines => _isCollapsed ? _collapsedLines : _lines;

    /// <summary>바로 아래 줄이 자식이다(접기·펼치기 단추를 보인다).</summary>
    public bool HasChildren => _lines.HasChildren;

    /// <summary>하위 트리를 접었다. 줄 목록에서 하위 단계를 빼는 일은 <see cref="PlanViewModel"/> 이 한다.</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        internal set
        {
            if (SetProperty(ref _isCollapsed, value))
            {
                OnPropertyChanged(nameof(TreeLines));
                OnPropertyChanged(nameof(ToggleGlyph));
                OnPropertyChanged(nameof(ToggleTip));
            }
        }
    }

    /// <summary>접힌 하위 단계 수(펼쳐 있어도 값은 유지한다).</summary>
    public int HiddenCount
    {
        get => _hiddenCount;
        internal set => SetProperty(ref _hiddenCount, value);
    }

    /// <summary>접힌 하위 단계 안에 가장 비싼 단계가 있다.</summary>
    public bool HiddenHottest
    {
        get => _hiddenHottest;
        internal set
        {
            if (SetProperty(ref _hiddenHottest, value))
            {
                OnPropertyChanged(nameof(HiddenTip));
            }
        }
    }

    public string ToggleGlyph => _isCollapsed ? "▸" : "▾";

    public string ToggleTip => _isCollapsed ? "하위 단계 펼치기(→)" : "하위 단계 접기(←)";

    public string HiddenTip => $"접힌 하위 단계 {HiddenCount}개" + (_hiddenHottest ? " · 가장 비싼 단계 포함" : string.Empty);

    /// <summary>실제 통계(A-Rows·시간)가 있는 계획이면 실제 열을 보인다.</summary>
    public bool HasActuals { get; }

    /// <summary>access 조건절(없으면 null).</summary>
    public string? AccessPredicates { get; }

    /// <summary>filter 조건절(없으면 null).</summary>
    public string? FilterPredicates { get; }

    public bool HasPredicates => AccessPredicates is not null || FilterPredicates is not null;

    public string ObjectText { get; }

    public string CostText { get; }

    public string EstimatedText { get; }

    public string ActualText { get; }

    public string StartsText { get; }

    public string TimeText { get; }

    public string Predicates { get; }

    public string RiskText { get; }

    public bool IsRisky => Step.Risk != PlanRisk.None;

    /// <summary>누적 비용 비중 막대 폭(옅은 막대).</summary>
    public double CostBarWidth { get; }

    /// <summary>자체 비용 비중 막대 폭(진한 막대).</summary>
    public double SelfCostBarWidth { get; }

    public string CostShareText { get; } = string.Empty;

    public string? CostTip { get; }

    /// <summary>실제 비중 막대 폭: 누적 A-Time(없으면 A-Rows).</summary>
    public double ActualBarWidth { get; }

    /// <summary>자체 A-Time 비중 막대 폭(A-Rows 기준이면 <see cref="ActualBarWidth"/> 와 같다).</summary>
    public double SelfActualBarWidth { get; }

    public string ActualShareText { get; } = string.Empty;

    public string? ActualTip { get; }

    /// <summary>가장 비싼 단계(자체 시간, 없으면 자체 비용이 가장 큰 단계).</summary>
    public bool IsHottest { get; }

    public string? HotTip { get; }

    /// <summary>위험 표시 문구(없으면 빈 문자열).</summary>
    public static string RiskDescription(PlanRisk risk)
    {
        var parts = new List<string>();
        if (risk.HasFlag(PlanRisk.FullScan))
        {
            parts.Add("전체 스캔");
        }

        if (risk.HasFlag(PlanRisk.Cartesian))
        {
            parts.Add("카테시안");
        }

        if (risk.HasFlag(PlanRisk.CardinalityMisestimate))
        {
            parts.Add("행 수 차이");
        }

        return string.Join(", ", parts);
    }

    /// <summary>연산 + 옵션(예: <c>TABLE ACCESS FULL</c>).</summary>
    internal static string OperationName(PlanStep step) =>
        string.IsNullOrEmpty(step.Options) ? step.Operation : step.Operation + " " + step.Options;

    /// <summary>소유자.객체(없으면 빈 문자열).</summary>
    internal static string ObjectName(PlanStep step) =>
        string.IsNullOrEmpty(step.ObjectName) ? string.Empty : (string.IsNullOrEmpty(step.ObjectOwner) ? step.ObjectName : step.ObjectOwner + "." + step.ObjectName);

    internal static string Fmt(long? v) => v?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>비중 → 「45%」. 0 보다 크고 1% 미만이면 「&lt;1%」.</summary>
    internal static string Percent(double share) =>
        share > 0 && share < 0.01 ? "<1%" : (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

    private static double Bar(double share) => Math.Round(Math.Clamp(share, 0, 1) * BarWidth, 1);
}

/// <summary>
/// <c>oranda.panel.plan</c> — 예상 계획(EXPLAIN PLAN)·실제 계획(DISPLAY_CURSOR) 트리, DBMS_XPLAN 텍스트, 형식 옵션(TSK-07-01, PLAN-01~05).
/// <para>서비스는 처음 쓸 때 꺼낸다(D-006). 팩이 필요한 형식은 쓰지 않는다.</para>
/// </summary>
[OrandaToolPanel("oranda.panel.plan", ToolPanelKind.Tool, DockLocation.Bottom, Order = 30)]
public sealed partial class PlanViewModel : ToolPanelViewModelBase
{
    /// <summary>기본 형식 선택지(DBMS_XPLAN 형식 문자열의 바탕).</summary>
    public static readonly IReadOnlyList<string> BaseFormats = ["BASIC", "TYPICAL", "SERIAL", "ALL", "ALLSTATS"];

    private readonly IServiceProvider? _services;
    private IExecutionPlanService? _plans;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private string _baseFormat = "ALLSTATS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private bool _lastOnly = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private bool _peekedBinds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private bool _alias;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private bool _outline;

    /// <summary>트리에서 고른 단계(상세 칸이 조건절을 보인다). 새 계획을 받으면 가장 비싼 단계를 고른다.</summary>
    [ObservableProperty]
    private PlanRowItem? _selectedRow;

    /// <summary>지금 계획에 실제 통계가 있으면 실제 열(A-Rows·Starts·시간·실제 비중)을 보인다.</summary>
    [ObservableProperty]
    private bool _hasActuals;

    [ObservableProperty]
    private string _planText = string.Empty;

    [ObservableProperty]
    private string _statusText = "실행 계획: SQL 편집기에서 Ctrl+E(예상), Ctrl+Shift+E(실제). " + ExplainNotice;

    [ObservableProperty]
    private string? _sqlIdText;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>고정한 비교 기준 계획 설명(없으면 빈 문자열).</summary>
    [ObservableProperty]
    private string _pinnedText = string.Empty;

    [ObservableProperty]
    private bool _hasPinned;

    /// <summary>비교 첫 줄: plan_hash_value 같음/다름.</summary>
    [ObservableProperty]
    private string _compareHashText = "비교할 계획이 없습니다. 트리에서 「이 계획 고정」 뒤 새 계획을 만드세요.";

    /// <summary>비교 둘째 줄: 전체 비용 변화와 단계 수.</summary>
    [ObservableProperty]
    private string _compareSummaryText = string.Empty;

    [ObservableProperty]
    private bool _compareHashDiffers;

    /// <summary>보이는 탭(0 트리, 1 텍스트, 2 비교, 3 그래프, 4 보고서).</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>시험·디자이너용: 서비스 없이 만든다.</summary>
    public PlanViewModel()
    {
        Current = this;
    }

    /// <summary>트리 표 칸 폭. 서비스로 만들면 앱 데이터 폴더의 <c>plan-columns.json</c> 에 남고, 아니면 메모리만 쓴다.</summary>
    public PlanColumnWidths Columns { get; init; } = new();

    /// <summary>셸(DI)이 쓰는 생성자.</summary>
    public PlanViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Current = this;
        if (services.GetService(typeof(Oranda.Core.Storage.IAppDataPaths)) is Oranda.Core.Storage.IAppDataPaths paths)
        {
            Columns = new PlanColumnWidths(paths.DataDirectory);
        }

        if (services.GetService(typeof(ICommandRegistry)) is ICommandRegistry registry)
        {
            // 「통계 보기」「자식 커서」 명령은 다른 기능이 등록한다 — 등록되면 단추가 나타난다.
            registry.CommandsChanged += (_, _) => PostToUi(RefreshCommandButtons);
        }

        // planAdvanced 끔이면 고급 보기·단추를 숨긴다(TSK-07-06). 게이트 Changed 로 바로 다시 본다.
        _features = services.GetService(typeof(Oranda.Core.Features.IFeatureGate)) as Oranda.Core.Features.IFeatureGate;
        if (_features is not null)
        {
            _features.Changed += OnFeaturesChanged;
        }
    }

    /// <summary>시험용: 서비스를 직접 준다.</summary>
    public PlanViewModel(IExecutionPlanService plans)
    {
        _plans = plans;
        Current = this;
    }

    /// <summary>가장 최근에 만든 계획 창(명령 처리기가 결과를 넣는 곳).</summary>
    public static PlanViewModel? Current { get; private set; }

    /// <summary>비교 탭 위치(<see cref="SelectedTabIndex"/>).</summary>
    public const int CompareTabIndex = 2;

    public ObservableCollection<PlanRowItem> Rows { get; } = [];

    /// <summary>고정한 계획과 지금 계획을 맞춘 줄(PLAN-04).</summary>
    public ObservableCollection<PlanCompareRowItem> CompareRows { get; } = [];

    /// <summary>지금 보이는 계획(없으면 null).</summary>
    public CorePlan? CurrentPlan { get; private set; }

    /// <summary>지금 보이는 계획의 종류(예상·실제(형식)).</summary>
    public string CurrentKind { get; private set; } = string.Empty;

    /// <summary>비교 기준으로 고정한 계획(없으면 null).</summary>
    public CorePlan? PinnedPlan { get; private set; }

    /// <summary>마지막 비교 결과(고정이 없거나 비교 전이면 null).</summary>
    public PlanComparison? Comparison { get; private set; }

    /// <summary>지금 계획의 비중·실행 순서(없으면 null).</summary>
    public PlanMetrics? Metrics { get; private set; }

    /// <summary>마지막으로 본 실제 계획의 커서.</summary>
    public CursorRef? LastCursor { get; private set; }

    /// <summary>UI 스레드로 넘기는 방법. 시험은 바로 실행하도록 바꾼다.</summary>
    public Action<Action> PostToUi { get; set; } = a =>
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            a();
        }
        else
        {
            Dispatcher.UIThread.Post(a);
        }
    };

    /// <summary>지금 옵션의 형식 문자열(툴바 형식 단추에 보인다).</summary>
    public string FormatText => BuildFormat();

    /// <summary>옵션 패널 값 → DBMS_XPLAN 형식 문자열(예: <c>ALLSTATS LAST +PEEKED_BINDS</c>).</summary>
    public string BuildFormat()
    {
        var parts = new List<string> { string.IsNullOrWhiteSpace(BaseFormat) ? "TYPICAL" : BaseFormat.Trim().ToUpperInvariant() };
        if (LastOnly && parts[0] == "ALLSTATS")
        {
            parts.Add("LAST");
        }

        if (PeekedBinds)
        {
            parts.Add("+PEEKED_BINDS");
        }

        if (Alias)
        {
            parts.Add("+ALIAS");
        }

        if (Outline)
        {
            parts.Add("+OUTLINE");
        }

        return string.Join(' ', parts);
    }

    /// <summary>편집기 현재 문장의 예상 계획을 만든다.</summary>
    public async Task ExplainAsync(ISession session, SqlStatement statement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(statement);
        await RunAsync("예상 계획", async plans => (await plans.ExplainAsync(session, statement, ct).ConfigureAwait(false), "예상"));
    }

    /// <summary>커서(sql_id·child)의 실제 계획을 형식 옵션대로 읽는다.</summary>
    public async Task ShowCursorAsync(ISession session, CursorRef cursor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(cursor);
        var format = BuildFormat();
        LastCursor = cursor;
        LastSession = session;
        await RunAsync("실제 계획", async plans => (await plans.GetCursorPlanAsync(session, cursor, format, ct).ConfigureAwait(false), $"실제({format})"));
    }

    /// <summary>입력칸의 sql_id 로 실제 계획을 읽는다.</summary>
    public Task ShowTypedCursorAsync(ISession session, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SqlIdText))
        {
            StatusText = "sql_id 를 입력하세요.";
            return Task.CompletedTask;
        }

        return ShowCursorAsync(session, new CursorRef(SqlIdText.Trim()), ct);
    }

    /// <summary>계획 결과를 창에 반영한다. 고정한 계획이 있으면 비교도 새로 한다(탭은 그대로).</summary>
    public void Apply(CorePlan plan, string kind)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var metrics = PlanMetrics.Compute(plan.Steps);
        var lines = PlanTreeLines.Compute([.. plan.Steps.Select(s => s.Depth)]);
        var rows = new List<PlanRowItem>(plan.Steps.Count);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            rows.Add(new PlanRowItem(plan.Steps[i], metrics.Steps[i], metrics.Actual, i == metrics.HottestIndex, lines[i]));
        }

        SelectedRow = null;
        _allRows = rows;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        UpdateCollapseState();

        HasActuals = metrics.Actual != PlanActualMetric.None;
        SelectedRow = metrics.HottestIndex >= 0 ? rows[metrics.HottestIndex] : null;

        CurrentPlan = plan;
        CurrentKind = kind;
        Metrics = metrics;
        PlanText = plan.Text;
        var risky = plan.Steps.Count(s => s.Risk != PlanRisk.None);
        Title = kind;
        StatusText = $"{kind} 계획 {plan.Steps.Count}단계" + (risky > 0 ? $", 위험 {risky}단계" : string.Empty)
            + HottestText(plan, metrics)
            + (plan.Cursor is { } c ? $" (sql_id {c.SqlId}, child {c.ChildNumber})" : string.Empty);

        if (PinnedPlan is not null)
        {
            RefreshComparison();
            if (Comparison is { } cmp)
            {
                StatusText += " · " + CompareStatus(cmp);
            }
        }

        AfterApply(plan);
    }

    /// <summary>지금 계획을 비교 기준으로 고정한다(<c>oranda.plan.pinForCompare</c>). 계획이 없으면 false.</summary>
    public bool PinCurrent()
    {
        if (!IsAdvancedEnabled)
        {
            return false; // 계획 고급 끔(TSK-07-06): 명령·단추가 숨지만 직접 불러도 고정하지 않는다
        }

        if (CurrentPlan is not { } plan)
        {
            StatusText = "고정할 계획이 없습니다. 먼저 예상(Ctrl+E) 또는 실제(Ctrl+Shift+E) 계획을 만드세요.";
            return false;
        }

        PinnedPlan = plan;
        HasPinned = true;
        var hash = PlanComparer.PlanHashOf(plan);
        PinnedText = $"고정: {CurrentKind} {plan.Steps.Count}단계"
            + (hash is { } h ? $", plan hash {h.ToString(CultureInfo.InvariantCulture)}" : string.Empty)
            + (plan.Cursor is { } c ? $", sql_id {c.SqlId}" : string.Empty)
            + $" ({DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";
        RefreshComparison();
        StatusText = "지금 계획을 비교 기준으로 고정했습니다. 새 계획을 만든 뒤 「비교」를 누르세요.";
        return true;
    }

    /// <summary>고정을 푼다.</summary>
    public void Unpin()
    {
        PinnedPlan = null;
        HasPinned = false;
        PinnedText = string.Empty;
        RefreshComparison();
        StatusText = "비교 기준 고정을 풀었습니다.";
    }

    /// <summary>고정한 계획과 지금 계획을 비교하고 비교 탭을 보인다(<c>oranda.plan.compare</c>). 비교하지 못하면 false.</summary>
    public bool CompareWithPinned()
    {
        if (!IsAdvancedEnabled)
        {
            return false;
        }

        if (PinnedPlan is null)
        {
            StatusText = "먼저 「이 계획 고정」으로 비교 기준 계획을 고정하세요.";
            return false;
        }

        if (CurrentPlan is null)
        {
            StatusText = "비교할 지금 계획이 없습니다. 예상 또는 실제 계획을 만드세요.";
            return false;
        }

        RefreshComparison();
        SelectedTabIndex = CompareTabIndex;
        StatusText = Comparison is { } cmp ? CompareStatus(cmp) : StatusText;
        return true;
    }

    /// <summary>고정·지금 계획으로 비교 목록과 요약을 다시 만든다.</summary>
    private void RefreshComparison()
    {
        CompareRows.Clear();
        Comparison = null;
        CompareHashDiffers = false;
        CompareSummaryText = string.Empty;
        if (PinnedPlan is not { } pinned)
        {
            CompareHashText = "비교할 계획이 없습니다. 트리에서 「이 계획 고정」 뒤 새 계획을 만드세요.";
            return;
        }

        if (CurrentPlan is not { } current || ReferenceEquals(pinned, current))
        {
            CompareHashText = "고정한 계획을 보고 있습니다. 새 계획을 만들면 여기서 비교합니다.";
            return;
        }

        var cmp = PlanComparer.Compare(pinned, current);
        Comparison = cmp;
        foreach (var row in cmp.Rows)
        {
            CompareRows.Add(new PlanCompareRowItem(row));
        }

        var hashes = $"{Hash(cmp.PinnedHash)} → {Hash(cmp.CurrentHash)}";
        (CompareHashText, CompareHashDiffers) = cmp.HashDiffers switch
        {
            true => ($"plan_hash_value 다름: {hashes} — 계획이 바뀌었습니다", true),
            false => ($"plan_hash_value 같음: {hashes}", false),
            null => ($"plan_hash_value 알 수 없음: {hashes}", false),
        };

        var cost = cmp.PinnedCost == cmp.CurrentCost
            ? $"전체 비용 {PlanRowItem.Fmt(cmp.CurrentCost)}"
            : $"전체 비용 {Cost(cmp.PinnedCost)} → {Cost(cmp.CurrentCost)}"
                + (PlanCompareRowItem.Delta(cmp.PinnedCost, cmp.CurrentCost) is { Length: > 0 } d ? $" ({d})" : string.Empty);
        CompareSummaryText = $"{cost} · 같음 {cmp.Count(PlanDiffKind.Same)} · 바뀜 {cmp.Count(PlanDiffKind.Changed)} · 추가 {cmp.Count(PlanDiffKind.Added)} · 빠짐 {cmp.Count(PlanDiffKind.Removed)}"
            + $" · 크게 바뀐 수치 {cmp.Rows.Count(r => r.CostChangedBig || r.RowsChangedBig || r.ActualRowsChangedBig)}줄"
            + (cmp.Positional ? " (단계가 많아 자리 순서로만 맞춤)" : string.Empty);

        static string Hash(long? h) => h?.ToString(CultureInfo.InvariantCulture) ?? "?";

        static string Cost(long? c) => c is null ? "?" : PlanRowItem.Fmt(c);
    }

    private static string CompareStatus(PlanComparison cmp) =>
        (cmp.HashDiffers switch { true => "고정 계획과 plan_hash_value 다름", false => "고정 계획과 plan_hash_value 같음", null => "고정 계획과 비교" })
        + $": 바뀜 {cmp.Count(PlanDiffKind.Changed)} · 추가 {cmp.Count(PlanDiffKind.Added)} · 빠짐 {cmp.Count(PlanDiffKind.Removed)}";

    private static string HottestText(CorePlan plan, PlanMetrics metrics)
    {
        if (metrics.HottestIndex < 0)
        {
            return string.Empty;
        }

        var m = metrics.Steps[metrics.HottestIndex];
        var share = metrics.Actual == PlanActualMetric.Time ? m.SelfActualShare : m.SelfCostShare;
        var basis = metrics.Actual == PlanActualMetric.Time ? "자체 시간" : "자체 비용";
        return $", 가장 비싼 단계 Id {plan.Steps[metrics.HottestIndex].Id}({basis} {PlanRowItem.Percent(share)})";
    }

    private async Task RunAsync(string what, Func<IExecutionPlanService, Task<(CorePlan Plan, string Kind)>> run)
    {
        try
        {
            var plans = _plans ??= _services?.GetService(typeof(IExecutionPlanService)) as IExecutionPlanService
                ?? throw new InvalidOperationException("실행 계획 서비스가 없습니다.");
            var (plan, kind) = await run(plans).ConfigureAwait(false);
            PostToUi(() => Apply(plan, kind));
        }
        catch (OperationCanceledException)
        {
            PostToUi(() => StatusText = what + " 취소됨");
        }
        catch (Exception ex)
        {
            var line = (ex.Message ?? string.Empty).Split('\n', 2)[0].Trim();
            PostToUi(() => StatusText = $"{what} 실패: {line}");
        }
    }
}
