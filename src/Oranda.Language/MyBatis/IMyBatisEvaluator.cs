using Oranda.Core.MyBatis;
using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>계산 선택 사항.</summary>
/// <param name="AllOn">
/// 값 없이 「모두 켬」으로 펼친다: if·when 은 켬, choose 는 첫 when, foreach 는 본문 한 번(항목 번호 없이 <c>:item</c>), bind 는 건너뜀.
/// 지금 「.xml 을 읽기 전용 SQL 로 펼치기」(ReadMyBatis) 결과와 같은 모양을 만들 때 쓴다(design.md 7장).
/// </param>
/// <param name="LegacyNotation">
/// 지금 ReadMyBatis 의 표기로 펼친다(<see cref="EvalResult.Sql"/> 만 달라진다): <c>#{a.b}</c> → <c>:a_b</c>, <c>${x}</c> → <c>&amp;x</c>(치환 변수),
/// if·when·choose·foreach·bind 는 조건을 <c>/* if … */</c> 같은 주석으로 남긴다. 기존 <c>WorkspaceFilesTests</c> 의 MyBatis 시험이 기대하는 모양이다(design.md 7장).
/// 보통은 <see cref="AllOn"/> 과 함께 쓴다.
/// </param>
public sealed record MyBatisEvalOptions(bool AllOn = false, bool LegacyNotation = false)
{
    public static MyBatisEvalOptions Default { get; } = new();
}

/// <summary>
/// 동적 SQL 계산기(MB-02, TRD 3.2·4). 계약(TSK-01-02) — 구현은 TSK-02-02(MyBatisEvaluator·OgnlLite).
/// 태그: if, choose/when/otherwise, where, set, trim, foreach, include(property), bind.
/// test 식 = OGNL 부분집합(null, 숫자·글자 리터럴, 비교 기호와 eq·neq·lt·lte·gt·gte, and·or·not·!, 괄호, 점 경로, size()·isEmpty()·length()·trim()·equals()).
/// 그 밖의 식은 <see cref="MyBatisEvalIssueKind.Unevaluable"/> 로 알리고 덮어쓰기로 정한다. 반사·동적 코드 생성·임의 메서드 호출은 쓰지 않는다.
/// 값(<c>values</c>)의 형: null, string, 숫자(int·long·decimal·double), bool, 목록(<see cref="System.Collections.IEnumerable"/>, string 제외),
/// 점 경로용 사전(<c>IReadOnlyDictionary&lt;string, object?&gt;</c>). 점이 든 평평한 열쇠(<c>"user.name"</c>, 시나리오 저장 모양)도 받으며,
/// 같은 경로가 둘 다 있으면 평평한 열쇠가 이긴다.
/// 모든 범위는 문장 원문(<see cref="MyBatisElement.Text"/>) 기준이다. UI 스레드에서 불러도 되게 빠르게(문장 500줄 50 ms 이하, PRD 6) 돈다.
/// </summary>
public interface IMyBatisEvaluator
{
    /// <summary>
    /// 문장을 값으로 펼친다. <paramref name="blockOverrides"/> = 블록 범위 → 켬·끔(계산 못 한 식, 조합 검사). 없으면 null.
    /// </summary>
    EvalResult Evaluate(
        MyBatisElement statement,
        IMyBatisFragmentResolver fragments,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<TextRange, bool>? blockOverrides,
        MyBatisEvalOptions? options = null);

    /// <summary>문장(include 한 조각 포함)에서 파라미터를 찾는다. 이름 순이 아니라 처음 나온 순서.</summary>
    IReadOnlyList<MyBatisParameter> ExtractParameters(MyBatisElement statement, IMyBatisFragmentResolver fragments);

    /// <summary>
    /// 조건 켬·끔 조합. 조건(<see cref="MyBatisBlock.IsCondition"/>) 수가 <paramref name="fullLimit"/> 이하면 전부,
    /// 넘으면 하나씩 끄기 + 모두 켬 + 모두 끔. <paramref name="allOn"/> 은 「모두 켬」 계산 결과(조건 블록 목록을 쓴다).
    /// </summary>
    IReadOnlyList<ConditionCombination> Combinations(EvalResult allOn, int fullLimit = 8);
}
