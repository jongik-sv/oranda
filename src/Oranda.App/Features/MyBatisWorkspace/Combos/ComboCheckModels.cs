using Oranda.Core.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>조합 하나의 검사 결과 종류.</summary>
public enum ComboOutcome
{
    /// <summary>구문 분석(고르면 실행 계획까지) 통과.</summary>
    Pass,

    /// <summary>통과로 세지만 알릴 것이 있다(빈 <c>&lt;set&gt;</c>, 실행 계획을 못 봄, 문법이 모르는 구문).</summary>
    Warning,

    /// <summary>구문 오류·계산 오류·실행 계획 오류.</summary>
    Error,
}

/// <summary>검사 방식(시안 「검사」 고르기).</summary>
public enum ComboCheckMode
{
    /// <summary>Oranda 구문 분석(L2)만. 접속이 필요 없다.</summary>
    Syntax,

    /// <summary>구문 분석을 통과한 조합을 실행 계획(EXPLAIN PLAN)으로도 확인한다. 접속이 필요하고 문장은 실행하지 않는다.</summary>
    SyntaxAndPlan,
}

/// <summary>조합 검사 조건 하나(표의 켬·끔 열). <paramref name="Range"/> = 문장 원문 안 블록 범위(덮어쓰기 열쇠).</summary>
/// <param name="Label">열 머리(if·when 은 test 식, foreach 는 「foreach 컬렉션」).</param>
/// <param name="Line">블록 머리 줄(1 기반, 문장 원문 기준).</param>
public sealed record ComboCondition(TextRange Range, string Label, int Line);

/// <summary>
/// 조합 하나(또는 시나리오 하나)의 검사 결과.
/// </summary>
/// <param name="Number">표의 번호(1 기반, 조합 다음에 시나리오).</param>
/// <param name="Title">「조합 2」 또는 「시나리오 이름」.</param>
/// <param name="States">조건마다 켬·끔(<see cref="ComboCheckReport.Conditions"/> 순서).</param>
/// <param name="ScenarioName">시나리오 값으로 검사했으면 그 이름, 조합이면 null.</param>
/// <param name="Outcome">결과.</param>
/// <param name="Reason">결과 칸 문구(통과면 빈 글자 또는 주의 문구).</param>
/// <param name="Detail">오른쪽 칸 설명(오류 문장, 원문 위치). 없으면 빈 글자.</param>
/// <param name="Sql">펼친 SQL.</param>
/// <param name="ErrorRange">펼친 SQL 안 오류 자리(강조). 없으면 null.</param>
/// <param name="SourceLine">원문(문장 원문) 안 원인 줄(1 기반). 모르면 null.</param>
public sealed record ComboCheckResult(
    int Number,
    string Title,
    IReadOnlyList<bool> States,
    string? ScenarioName,
    ComboOutcome Outcome,
    string Reason,
    string Detail,
    string Sql,
    TextRange? ErrorRange,
    int? SourceLine)
{
    /// <summary>시나리오 값으로 검사했다.</summary>
    public bool IsScenario => ScenarioName is not null;
}

/// <summary>문장 하나의 조합 검사 결과 전체.</summary>
/// <param name="StatementId">문장 id.</param>
/// <param name="Conditions">조건(표의 켬·끔 열).</param>
/// <param name="Results">조합 결과 다음에 시나리오 결과. 취소되면 그때까지의 결과.</param>
/// <param name="CombinationCount">만든 조합 수(취소돼도 전체 수).</param>
/// <param name="Reduced">조건이 많아 「하나씩 끄기 + 모두 켬 + 모두 끔」으로 줄였다.</param>
/// <param name="Cancelled">취소됐다.</param>
/// <param name="Mode">검사 방식.</param>
/// <param name="Notice">검사 전체에 대한 알림(실행 계획을 못 쓴 이유 등). 없으면 null.</param>
public sealed record ComboCheckReport(
    string StatementId,
    IReadOnlyList<ComboCondition> Conditions,
    IReadOnlyList<ComboCheckResult> Results,
    int CombinationCount,
    bool Reduced,
    bool Cancelled,
    ComboCheckMode Mode,
    string? Notice = null)
{
    public int ErrorCount => Results.Count(r => r.Outcome == ComboOutcome.Error);

    public int PassCount => Results.Count - ErrorCount;
}
