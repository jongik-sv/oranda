using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Access;

namespace Oranda.App.Features.Ai.Tuning.Commands;

/// <summary>
/// 「SQL 설명」 명령: 커서 위치 문장을 사람 말로 풀어 준다. 자유 글 답만 내고(구조 결과·제안 반영·레포트 없음), 도구는 <c>describe_object</c> 만 열린다
/// (상한 <see cref="AiAccessLevel.Metadata"/>). 문장을 실행하지 않고 재작성·인덱스 제안도 하지 않는다.
/// </summary>
public sealed class ExplainSqlCommand : AiCommandSpec
{
    /// <summary>메뉴 활성 식. 이 명령을 켜는 항목이 이 상수만 바꾼다(<c>AiCommands.cs</c> 는 고치지 않는다).</summary>
    public const string Enablement = AiCommands.TuneEnablement;

    /// <summary>이 명령이 한 대화에서 부를 도구의 상한(프롬프트 글).</summary>
    internal const int ToolBudget = 5;

    /// <summary>문장이 없을 때 대화 창에 남기는 안내.</summary>
    internal const string NoStatementText = "설명할 SQL 문장이 없어 보내지 않았습니다. 편집기에서 문장 위에 커서를 놓고 다시 시도하세요.";

    /// <summary>가린 대화에서 대화 창 첫 줄 뒤에 붙는 안내(주석 본문이 빠져 설명이 약해질 수 있다).</summary>
    internal const string MaskedSentNote = "주석 본문도 보내지 않으므로 주석에 의존한 설명은 약할 수 있습니다.";

    public static ExplainSqlCommand Instance { get; } = new();

    public override string Id => AiCommands.ExplainSql;

    public override string ConversationTitle => "SQL 설명";

    public override string UserLine => "SQL 설명";

    public override AiAccessLevel AccessCeiling => AiAccessLevel.Metadata;

    public override string? SentNote(bool masked) => masked ? MaskedSentNote : null;

    /// <summary>첫 요청과 모든 이어 말하기 뒤에 붙는 응답 규칙(자유 글, 재작성 없음).</summary>
    public override string ResponseRules =>
        "Response style (applies to every answer, including follow-ups): write in Korean and be brief. "
        + "1) One bold line saying what the statement does, for example **부서별 최근 30일 주문 합계 조회**. "
        + "2) ## 읽는 곳: one bullet per table or view, with what it holds, as far as known. "
        + "3) ## 처리 순서: 3 to 6 numbered steps in the order the statement works (filter, join, group, sort). "
        + "4) ## 주의: only real pitfalls seen in the text (implicit conversion, filter on the outer side of an outer join, NULL handling); skip if none. "
        + "No preamble or closing remarks, do not narrate tool calls, and never write tool or function names in the answer.";

    public override Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct)
    {
        System.ArgumentNullException.ThrowIfNull(env);
        return Task.FromResult(env.CursorStatement is { } statement && !string.IsNullOrWhiteSpace(statement.Text)
            ? AiCommandPreparation.Ready(new AiCommandTarget(statement, TabId: env.ActiveTabId))
            : AiCommandPreparation.Refuse(NoStatementText));
    }

    /// <summary>지시 글. 원문 문장이나 대상 상태는 읽지 않고, 가림 여부(<see cref="AiCommandPromptInput.Masked"/>)로만 갈린다.</summary>
    public override string BuildInstruction(AiCommandPromptInput input)
    {
        System.ArgumentNullException.ThrowIfNull(input);
        var text = "Explain this Oracle SQL statement to the user in plain Korean: what it reads, how the tables are joined and filtered, and what the result means. "
            + "Use describe_object only when the meaning of a table or column is unclear; do not guess names. "
            + $"Call at most {ToolBudget} tools. Answer in Korean. "
            + "Do not run the statement and do not suggest rewrites or indexes (if the user wants tuning, tell them to use 「AI 튜닝」).";
        return input.Masked
            ? text + " Literals and comments were removed before sending, so values appear as placeholders and comment text is missing; do not guess the hidden values, and say so briefly if the meaning depends on them."
            : text;
    }
}
