using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Access;

namespace Oranda.App.Features.Ai.Tuning.Commands;

/// <summary>
/// 「실행 계획 해설」 명령: 커서 위치 문장의 실행 계획을 사람 말로 풀어 준다. 자유 글 답만 내고(구조 결과·제안 반영·레포트 없음), 상한은 <see cref="AiAccessLevel.Plan"/>
/// (벤치마크는 닫힌다). <b>화면 계획 글(<c>plan.Text</c>)과 술어는 프롬프트에 넣지 않는다</b>: 계획 창에 커서가 있고 그 계획이 이 문장의 것일 때만 <c>sql_id</c> 를
/// 문맥에 실어, AI 가 <c>get_cursor_plan</c> 으로(허용 수준·술어 가림·감사를 거쳐) 읽게 한다. 문장을 다시 실행하지 않는다.
/// </summary>
public sealed class ExplainPlanCommand : AiCommandSpec
{
    /// <summary>메뉴 활성 식. 이 명령을 켜는 항목이 이 상수만 바꾼다(<c>AiCommands.cs</c> 는 고치지 않는다).</summary>
    public const string Enablement = AiCommands.TuneEnablement + " && aiAccessPlan";

    /// <summary>이 명령이 한 대화에서 부를 도구의 상한(프롬프트 글).</summary>
    internal const int ToolBudget = 5;

    /// <summary>문장이 없을 때 대화 창에 남기는 안내.</summary>
    internal const string NoStatementText = "해설할 SQL 문장이 없어 보내지 않았습니다. 편집기에서 문장 위에 커서를 놓고 다시 시도하세요.";

    /// <summary>맞는 커서 계획이 없고 예상 계획도 읽을 수 없을 때(이 접속의 EXPLAIN PLAN 허용이 꺼짐) 대화 창에 남기는 안내.</summary>
    internal const string NoReadablePlanText = "읽을 수 있는 실행 계획이 없어 해설하지 않았습니다. 계획 창의 커서 계획이 이 문장의 것이 아니고, 이 접속은 EXPLAIN PLAN 허용이 꺼져 있습니다. 문장을 실행해 계획을 확인하거나 접속 편집에서 허용을 켠 뒤 다시 시도하세요.";

    /// <summary>대화 창 첫 줄 뒤에 붙는 안내(커서 계획 사용 여부는 이 시점에 알 수 없으므로 가림 여부만 갈린다).</summary>
    internal const string SentNoteText = "화면의 계획 글은 보내지 않고, AI 가 허용된 도구로 계획을 직접 읽습니다.";

    private readonly Func<Guid?, ScreenPlan?> _readScreenPlan;

    public ExplainPlanCommand()
        : this(ReadScreenPlanFromPanel)
    {
    }

    /// <summary>시험용: 계획 창을 읽는 길을 바꾼다.</summary>
    internal ExplainPlanCommand(Func<Guid?, ScreenPlan?> readScreenPlan)
    {
        _readScreenPlan = readScreenPlan ?? throw new ArgumentNullException(nameof(readScreenPlan));
    }

    /// <summary>계획 창이 그 탭에서 보이는 계획의 <c>sql_id</c> 와 DBMS_XPLAN 글(문장 대조에만 쓰고 AI 로 보내지 않는다).</summary>
    internal readonly record struct ScreenPlan(string? SqlId, string? Text);

    public static ExplainPlanCommand Instance { get; } = new();

    public override string Id => AiCommands.ExplainPlan;

    public override string ConversationTitle => "실행 계획 해설";

    public override string UserLine => "실행 계획 해설";

    public override AiAccessLevel AccessCeiling => AiAccessLevel.Plan;

    public override string? SentNote(bool masked) => SentNoteText;

    /// <summary>첫 요청과 모든 이어 말하기 뒤에 붙는 응답 규칙(자유 글, 재작성 없음).</summary>
    public override string ResponseRules =>
        "Response style (applies to every answer, including follow-ups): write in Korean and be brief. "
        + "1) One bold line with the main finding, for example **ORDERS 전체 스캔이 비용의 80%**. "
        + "2) ## 계획 요약: 3 to 6 bullets walking the plan from the innermost step outwards, each naming the operation and the object. "
        + "3) ## 눈여겨볼 곳: up to 3 bullets with numbers (cost, E-Rows, A-Rows, buffers); mark estimates as 추정. "
        + "4) One line starting with > that says what the plan does not tell (for example the format differs from the screen, or statistics may be stale). "
        + "No preamble or closing remarks, do not narrate tool calls, and never write tool or function names in the answer; say 실행 계획 or 튜닝 정보 instead.";

    public override Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (env.CursorStatement is not { } statement || string.IsNullOrWhiteSpace(statement.Text))
        {
            return Task.FromResult(AiCommandPreparation.Refuse(NoStatementText));
        }

        // 화면 계획은 sql_id 를 얻는 데만 쓴다: 이 문장의 계획일 때만(다른 문장의 계획이면 쓰지 않는다). 글 자체는 어디에도 넘기지 않는다.
        string? sqlId = null;
        // 머리(SQL_ID 줄)가 없는 글(BASIC 형식 등)은 Matches 가 「확인 불가」 로 통과시키지만, 여기서는 다른 문장의 sql_id 를 싣지 않도록 머리가 있어야 믿는다.
        // 치환 변수(&)가 있는 문장은 편집기 글과 서버 글이 달라 맞지 않는다: 안전하게 sql_id 없이 간다.
        if (_readScreenPlan(env.ActiveTabId) is { SqlId: { Length: > 0 } shownId } shown
            && PlanSqlMatch.ExtractSql(shown.Text) is not null
            && IsPlanOfStatement(shown.Text, statement.Text))
        {
            sqlId = shownId;
        }

        // 읽을 길이 없다: 커서도 없고 explain_plan 도 허용되지 않는다.
        if (sqlId is null && !AiAccessPolicy.ExplainPlanEnabled(env.Profile))
        {
            return Task.FromResult(AiCommandPreparation.Refuse(NoReadablePlanText));
        }

        return Task.FromResult(AiCommandPreparation.Ready(new AiCommandTarget(statement, sqlId, env.ActiveTabId)));
    }

    /// <summary>지시 글. 원문 문장이나 대상 상태는 읽지 않는다(<c>sql_id</c> 유무와 가림 여부로만 갈린다).</summary>
    public override string BuildInstruction(AiCommandPromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var read = input.Target.SqlId is not null
            ? "Read the plan with the Oranda tools, the cursor plan first (get_cursor_plan); do not guess. "
            : "No cached cursor plan is available for this statement, so read the expected plan with explain_plan and say in the first line of the answer that it is an estimate, not the plan that actually ran. ";
        return "Explain the execution plan of this Oracle SQL statement to the user in plain Korean. "
            + read
            + "Say which steps cost the most, why the optimizer chose this access path and join order, and where estimated and actual rows differ (E-Rows vs A-Rows) when actual rows are available. "
            + "Do not run the statement to get actual rows. "
            + $"Call at most {ToolBudget} tools. Answer in Korean. "
            + "Do not suggest rewrites or indexes (if the user wants tuning, tell them to use 「AI 튜닝」).";
    }

    /// <summary>
    /// 화면 계획이 이 문장의 것인가. 「실제 계획(A-Rows)」 은 서버에 힌트(GATHER_PLAN_STATISTICS)를 넣은 사본을 보내므로 서버 글에는 힌트가 있고 편집기 글에는 없다.
    /// 그래서 편집기 글 그대로와 <see cref="PlanStatisticsHint"/> 사본 두 가지만 같은 문장으로 인정한다. 힌트 외의 차이는 <see cref="PlanSqlMatch"/> 가 그대로 거른다.
    /// </summary>
    private static bool IsPlanOfStatement(string? planText, string statementText) =>
        PlanSqlMatch.Matches(planText, statementText)
        || (PlanStatisticsHint.AddTo(statementText) is { } hinted && PlanSqlMatch.Matches(planText, hinted));

    private static ScreenPlan? ReadScreenPlanFromPanel(Guid? tabId)
    {
        if (tabId is not { } id || PlanPanelViewModel.Instance is not { } panel || !panel.Has(id))
        {
            return null; // 계획 창이 없거나(기능 꺼짐) 이 탭의 계획이 없다 → 커서 없음으로 본다
        }

        var tab = SqlEditorHost.Current?.Tabs.FirstOrDefault(t => t.TabId == id);
        return tab is not null && panel.For(tab).CurrentPlan is { } plan ? new ScreenPlan(plan.Cursor?.SqlId, plan.Text) : null;
    }
}
