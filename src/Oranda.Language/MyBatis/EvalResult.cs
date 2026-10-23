using Oranda.Core.Text;

namespace Oranda.Language.MyBatis;

/// <summary>
/// 펼친 SQL 의 바인드 하나. 이름 규칙: <c>#{x}</c> → <c>:x</c>, 점 경로 <c>#{user.name}</c> → <c>:user_name</c>(Oracle 바인드 이름에 점을 못 쓴다),
/// foreach 안 <c>#{item}</c> → <c>:item_1</c>, <c>:item_2</c>…(반복 순서). 이름이 겹치면 뒤에 <c>_2</c>… 를 붙여 겹치지 않게 한다.
/// </summary>
/// <param name="Name">콜론 없는 바인드 이름(펼친 SQL 에 쓰인 그대로).</param>
/// <param name="Parameter">값을 꺼낸 파라미터 경로(<c>deptIds[0]</c>, <c>user.name</c>).</param>
/// <param name="JdbcType"><c>#{x,jdbcType=…}</c> 의 타입 힌트. 없으면 null.</param>
/// <param name="Value">값(<see cref="IMyBatisEvaluator"/> 가 받은 형 그대로, null 가능).</param>
/// <param name="Source">문장 원문 안 <c>#{…}</c> 범위. include 한 조각 안이면 그 <c>&lt;include&gt;</c> 태그 범위(<paramref name="Fragment"/> 참고).</param>
/// <param name="Fragment">include 한 sql 조각 안에서 나왔으면 그 refid, 문장 원문이면 null.</param>
/// <param name="FragmentRange">조각 원문(<see cref="Oranda.Core.MyBatis.MyBatisFragment.Text"/>) 안 범위. 문장 원문이면 null.</param>
public sealed record MyBatisBind(
    string Name, string Parameter, string? JdbcType, object? Value, TextRange Source, string? Fragment = null, TextRange? FragmentRange = null);

/// <summary>펼친 SQL 의 치환 하나(<c>${x}</c> → 값 글자). 실행 전 값을 보이고 운영 접속이면 확인한다(MB-05).</summary>
/// <param name="Name">파라미터 경로.</param>
/// <param name="Value">넣은 글자. 값이 null 이면 빈 글자를 넣고 null 로 남긴다.</param>
/// <param name="Source">문장 원문 안 <c>${…}</c> 범위. include 한 조각 안이면 그 <c>&lt;include&gt;</c> 태그 범위.</param>
/// <param name="Fragment">include 한 조각 안이면 그 refid.</param>
/// <param name="FragmentRange">조각 원문 안 범위.</param>
public sealed record MyBatisSubstitution(string Name, string? Value, TextRange Source, string? Fragment = null, TextRange? FragmentRange = null);

/// <summary>
/// 동적 태그 블록 하나와 이번 계산의 켬·끔(MB-02·MB-05). 편집기가 끈 블록을 흐리게 하고 if·when 머리 줄에 점을 찍는다.
/// <para>
/// 블록은 원문 태그마다 하나다(foreach 를 여러 번 돌아도, 바깥 foreach 안의 if 가 여러 번 계산돼도 하나. 그 경우 <see cref="IsOn"/> 은 한 번이라도 켜졌으면 참).
/// 덮어쓰기: if·when 은 test 결과 대신 켬·끔. foreach 는 끔 = 빈 목록으로 봄, 켬 = 값 목록 그대로(값이 없거나 빈 목록이면 항목 하나로 봄).
/// include 한 조각 안의 블록은 <see cref="Range"/> 가 그 <c>&lt;include&gt;</c> 태그 범위이고 <see cref="Fragment"/> 가 있다. 따로 덮어쓸 수 없고 조합 검사 조건도 아니다(<see cref="IsCondition"/> 거짓).
/// </para>
/// </summary>
/// <param name="Range">문장 원문 안 블록 전체(여는 태그 ~ 닫는 태그). <see cref="IMyBatisEvaluator.Evaluate"/> 의 덮어쓰기 열쇠도 이 범위다.</param>
/// <param name="Tag">태그 이름(<c>if</c>·<c>when</c>·<c>otherwise</c>·<c>foreach</c>·<c>where</c>·<c>set</c>·<c>trim</c>·<c>choose</c>·<c>include</c>·<c>bind</c>).</param>
/// <param name="Test">test 식(if·when). 없으면 null.</param>
/// <param name="IsOn">이번 값으로 켜졌다(where·set·trim 은 내용이 남았는지, foreach 는 항목이 있는지).</param>
/// <param name="IsOverridden">계산이 아니라 덮어쓰기로 정했다.</param>
/// <param name="IsCondition">조합 검사의 조건이다(문장 원문의 if·when, 그리고 foreach 빈 목록/값 있음).</param>
/// <param name="Fragment">include 한 조각 안 블록이면 그 refid.</param>
/// <param name="FragmentRange">조각 원문 안 블록 범위.</param>
public sealed record MyBatisBlock(
    TextRange Range, string Tag, string? Test, bool IsOn, bool IsOverridden, bool IsCondition, string? Fragment = null, TextRange? FragmentRange = null);

/// <summary>계산 문제 종류.</summary>
public enum MyBatisEvalIssueKind
{
    /// <summary>지원하지 않는 식(OGNL 부분집합 밖). 덮어쓰기로 켬·끔을 정할 수 있다.</summary>
    Unevaluable,

    /// <summary>include 조각을 찾지 못했다.</summary>
    FragmentMissing,

    /// <summary>include 가 돌고 돈다.</summary>
    IncludeCycle,

    /// <summary>태그·속성이 잘못됐다(foreach collection 없음 등).</summary>
    InvalidTag,
}

/// <summary>계산 문제 하나. <paramref name="Range"/> 는 문장 원문 기준(물결 밑줄 자리, 조각 안이면 그 include 태그).</summary>
public sealed record MyBatisEvalIssue(
    MyBatisEvalIssueKind Kind, TextRange Range, string? Expression, string Reason, string? Fragment = null, TextRange? FragmentRange = null);

/// <summary>
/// 계산 결과(MB-02, TRD 3.2). 계약(TSK-01-02) — 구현은 TSK-02-02.
/// 모든 범위는 문장 원문(<see cref="MyBatisElement.Text"/>) 기준이다.
/// </summary>
/// <param name="Sql">펼친 SQL(바인드는 <c>:이름</c>, 치환은 값 글자).</param>
/// <param name="Binds">바인드(펼친 SQL 에 나온 순서).</param>
/// <param name="Substitutions">치환(나온 순서).</param>
/// <param name="Blocks">동적 태그 블록(원문 순서, 바깥 블록이 먼저).</param>
/// <param name="Issues">계산 못 한 식·조각 없음·순환 등.</param>
public sealed record EvalResult(
    string Sql,
    IReadOnlyList<MyBatisBind> Binds,
    IReadOnlyList<MyBatisSubstitution> Substitutions,
    IReadOnlyList<MyBatisBlock> Blocks,
    IReadOnlyList<MyBatisEvalIssue> Issues);
