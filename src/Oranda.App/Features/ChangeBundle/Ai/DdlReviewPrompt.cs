using System.Globalization;
using System.Text;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;

namespace Oranda.App.Features.ChangeBundle.Ai;

/// <summary>검토 대상 객체 하나(<c>OWNER.NAME</c>). 도구가 볼 수 있는 범위가 된다.</summary>
public sealed record DdlReviewTarget(string Owner, string Name)
{
    public string Key => Owner + "." + Name;
}

/// <summary>
/// 실행 전 검토 한 번의 재료(화면의 지금 상태). 변경 묶음 실행 창·테이블 편집 DDL 미리 보기가 만든다.
/// </summary>
/// <param name="Profile">실행할 접속.</param>
/// <param name="Script">실행할 문장(소유자 바꾸기 뒤, 화면 순서).</param>
/// <param name="AppFindings">앱이 정해진 규칙으로 낸 검사 줄(✖·⚠·✔). AI 는 이것을 고치지 않고 덧붙인다.</param>
/// <param name="Targets">문장이 바꾸는 객체.</param>
/// <param name="Server">서버 한 줄(「Oracle 19」 등, 모르면 null).</param>
public sealed record DdlReviewInput(
    ConnectionProfile Profile, string Script, IReadOnlyList<string> AppFindings, IReadOnlyList<DdlReviewTarget> Targets, string? Server = null);

/// <summary>답의 위험도.</summary>
public enum DdlRisk
{
    Low,
    Medium,
    High,
}

/// <summary>
/// 실행 전 검토(설계 §4.1, TSK-15-09)의 지시 글·답 틀·꾸러미. 답 틀 = 위험도(낮음·보통·높음) + 점검 항목 표(잠금·공간·시간·무효화·되돌리기) + 권하는 순서·시간대.
/// AI 는 실행하지 않는다. 앱의 검사 줄은 그대로 두고 그 위에 덧붙인다.
/// </summary>
public static class DdlReviewPrompt
{
    /// <summary>감사 기록 기능 이름(<c>ai_kit:ddl_review</c>).</summary>
    public const string Feature = "ddl_review";

    /// <summary>띠 이름.</summary>
    public const string Title = "실행 전 검토";

    public const string RiskSection = "위험도";

    public const string ItemsSection = "점검 항목";

    public const string OrderSection = "권하는 순서";

    /// <summary>점검 항목 표에 꼭 있어야 할 줄(차례대로).</summary>
    public static IReadOnlyList<string> CheckRows { get; } = ["잠금", "공간", "시간", "무효화", "되돌리기"];

    /// <summary>답 틀.</summary>
    public static AiAnswerTemplate Template { get; } = new([
        new AiAnswerSection(RiskSection, "first word exactly one of 낮음, 보통, 높음, then one short reason"),
        new AiAnswerSection(ItemsSection, "rows 잠금, 공간, 시간, 무효화, 되돌리기 in this order; 판단 is a short verdict, 근거 is the evidence from the tools or the script", ["항목", "판단", "근거"]),
        new AiAnswerSection(OrderSection, "the recommended order of execution and time window in one or two sentences"),
    ]);

    /// <summary>지시 글(영어). 도구로 의존 객체·크기를 보게 한다.</summary>
    public static string Instruction(DdlReviewInput input, AiAccessLevel level)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = new StringBuilder();
        text.AppendLine("You are reviewing DDL that a DBA is about to run, as a checklist at the moment of execution. You never execute anything; the user decides and runs it.");
        text.AppendLine("Judge lock duration, extra space, run time, objects that become invalid and whether it can be undone, for each statement together.");
        if (input.Targets.Count > 0)
        {
            text.Append("Before answering, call get_dependents for each changed object (").Append(string.Join(", ", input.Targets.Select(t => t.Key))).Append(')');
            text.AppendLine(level >= AiAccessLevel.Plan ? " and get_table_stats for its size." : ". Sizes are not available at this access level; say so instead of guessing.");
        }

        text.AppendLine("The app already shows its own rule-based check lines (given below). Do not repeat or contradict them; add only what they miss.");
        text.AppendLine("Keep it short: only risky points matter.");
        return text.ToString().TrimEnd();
    }

    /// <summary>꾸러미: 접속 사실 · 바꾸는 객체 · 앱 검사 줄 · 문장(SQL 칸이라 가림 대상 접속이면 리터럴을 가린다). 모두 메타데이터 수준.</summary>
    public static IAiContextBundle BuildBundle(AiKitRoute route, DdlReviewInput input, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(input);
        var connection = string.Create(CultureInfo.InvariantCulture,
            $"dbms={input.Profile.Dbms}; environment={input.Profile.Environment}{(string.IsNullOrWhiteSpace(input.Server) ? string.Empty : "; server=" + input.Server)}");
        return route.Bundle(Title, masker)
            .Add("connection", "접속", connection, AiAccessLevel.Metadata)
            .Add("targets", "바꾸는 객체", string.Join("\n", input.Targets.Select(t => t.Key)), AiAccessLevel.Metadata)
            .Add("app_checks", "앱 검사 줄", string.Join("\n", input.AppFindings), AiAccessLevel.Metadata)
            .Add("script", "실행할 문장", input.Script, AiAccessLevel.Metadata, AiContextKind.Sql)
            .Build();
    }

    /// <summary>답의 위험도 칸 첫 낱말(낮음·보통·높음). 틀이 깨졌거나 모르면 null.</summary>
    public static DdlRisk? RiskOf(AiTemplatedAnswer? answer)
    {
        if (answer?[RiskSection]?.Body is not { Length: > 0 } body)
        {
            return null;
        }

        var head = body.TrimStart('*', ' ', '\t', '-');
        return head.StartsWith("높음", StringComparison.Ordinal) ? DdlRisk.High
            : head.StartsWith("보통", StringComparison.Ordinal) ? DdlRisk.Medium
            : head.StartsWith("낮음", StringComparison.Ordinal) ? DdlRisk.Low
            : null;
    }

    /// <summary>위험도 표시 글(「위험도 보통」).</summary>
    public static string RiskText(DdlRisk risk) => risk switch
    {
        DdlRisk.High => "위험도 높음",
        DdlRisk.Medium => "위험도 보통",
        _ => "위험도 낮음",
    };

    /// <summary>
    /// 도구가 볼 수 있는 범위를 바꾸는 객체로 넓힌 실행 범위(DDL 은 문장 참조 객체를 뽑지 못하므로 「보낼 범위」 에 정확한 이름만 넣는다).
    /// 원래 세션은 쓰지 않으므로 폐기한다.
    /// </summary>
    public static AgentSession Widen(AgentSession session, IReadOnlyList<DdlReviewTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            return session;
        }

        var widened = new AgentSession(session.Run with { Send = new AgentSendScope(false, [.. targets.Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase)]) });
        session.Revoke();
        session.Dispose();
        return widened;
    }
}
