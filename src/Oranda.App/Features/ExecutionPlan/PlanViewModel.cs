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

/// <summary>계획 트리 한 줄(PLAN-01·03). 위험 단계는 <see cref="RiskText"/> 로 강조한다.</summary>
public sealed class PlanRowItem(PlanStep step)
{
    public PlanStep Step { get; } = step;

    public int Id => Step.Id;

    /// <summary>깊이만큼 들여쓴 연산 이름.</summary>
    public string Operation => new string(' ', Step.Depth * 2) + Step.Operation + (string.IsNullOrEmpty(Step.Options) ? string.Empty : " " + Step.Options);

    public string ObjectText => string.IsNullOrEmpty(Step.ObjectName) ? string.Empty : (string.IsNullOrEmpty(Step.ObjectOwner) ? Step.ObjectName : Step.ObjectOwner + "." + Step.ObjectName);

    public string CostText => Fmt(Step.Cost);

    public string EstimatedText => Fmt(Step.EstimatedRows);

    public string ActualText => Fmt(Step.ActualRows);

    public string StartsText => Fmt(Step.Starts);

    public string TimeText => Step.ActualTime is { } t ? t.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) : string.Empty;

    public string Predicates => string.Join(" | ", new[]
    {
        Step.AccessPredicates is { Length: > 0 } a ? "access: " + a : null,
        Step.FilterPredicates is { Length: > 0 } f ? "filter: " + f : null,
    }.Where(x => x is not null));

    public string RiskText => RiskDescription(Step.Risk);

    public bool IsRisky => Step.Risk != PlanRisk.None;

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

    private static string Fmt(long? v) => v?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;
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
    private string _baseFormat = "ALLSTATS";

    [ObservableProperty]
    private bool _lastOnly = true;

    [ObservableProperty]
    private bool _peekedBinds;

    [ObservableProperty]
    private bool _alias;

    [ObservableProperty]
    private bool _outline;

    [ObservableProperty]
    private string _planText = string.Empty;

    [ObservableProperty]
    private string _statusText = "실행 계획: SQL 편집기에서 Ctrl+E(예상), Ctrl+Shift+E(실제).";

    [ObservableProperty]
    private string? _sqlIdText;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>시험·디자이너용: 서비스 없이 만든다.</summary>
    public PlanViewModel()
    {
        Current = this;
    }

    /// <summary>셸(DI)이 쓰는 생성자.</summary>
    public PlanViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        Current = this;
    }

    /// <summary>시험용: 서비스를 직접 준다.</summary>
    public PlanViewModel(IExecutionPlanService plans)
    {
        _plans = plans;
        Current = this;
    }

    /// <summary>가장 최근에 만든 계획 창(명령 처리기가 결과를 넣는 곳).</summary>
    public static PlanViewModel? Current { get; private set; }

    public ObservableCollection<PlanRowItem> Rows { get; } = [];

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

    /// <summary>계획 결과를 창에 반영한다.</summary>
    public void Apply(CorePlan plan, string kind)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Rows.Clear();
        foreach (var s in plan.Steps)
        {
            Rows.Add(new PlanRowItem(s));
        }

        PlanText = plan.Text;
        var risky = plan.Steps.Count(s => s.Risk != PlanRisk.None);
        Title = kind;
        StatusText = $"{kind} 계획 {plan.Steps.Count}단계" + (risky > 0 ? $", 위험 {risky}단계" : string.Empty)
            + (plan.Cursor is { } c ? $" (sql_id {c.SqlId}, child {c.ChildNumber})" : string.Empty);
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
