using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema;

/// <summary>열 순서 바꾸는 방식(TSK-13-01).</summary>
public enum ColumnReorderMethod
{
    /// <summary>12c 이상: 열마다 INVISIBLE → VISIBLE (메타데이터만 바뀌어 큰 표도 즉시 끝난다).</summary>
    InvisibleVisible,

    /// <summary>재생성 스크립트: Enterprise Edition 이면 DBMS_REDEFINITION, 아니면 CTAS + RENAME. 스크립트만 만든다.</summary>
    Rebuild,
}

/// <summary>열 순서를 바꾸면 결과가 달라질 수 있는 소스 줄의 종류.</summary>
public enum ColumnReorderRisk
{
    /// <summary><c>SELECT *</c> (또는 <c>t.*</c>): 열 순서가 곧 결과 열 순서다.</summary>
    SelectStar,

    /// <summary>열 목록 없는 <c>INSERT INTO 표 VALUES (…)</c> 또는 <c>INSERT INTO 표 SELECT …</c>: 값이 열 위치로 들어간다.</summary>
    InsertWithoutColumns,
}

/// <summary>위험 후보 한 줄.</summary>
public sealed record ColumnReorderRiskHit(DbObjectRef Ref, int Line, ColumnReorderRisk Risk, string Text);

/// <summary>
/// 열 순서 바꾸기 스크립트 만들기. 문장·텍스트만 만들고 실행하지 않는다 — 실행은 반드시 <c>IExecutionService</c>(정책 관문)로 보낸다.
/// DBMS 마다 하나씩 구현한다(Oracle = Oranda.Oracle, Tibero = Oranda.Tibero).
/// 구현은 <c>[OrandaService(typeof(IColumnReorderScripts), Dbms = …)]</c> 로 등록하고, 쓰는 쪽은
/// DBMS 별 구현 고르기(<c>IDbmsServiceSelector</c>)로 세션의 DBMS 에 맞는 것을 꺼낸다. 소유: TSK-13-01.
/// </summary>
public interface IColumnReorderScripts
{
    /// <summary>이 구현이 맡는 DBMS.</summary>
    Dbms Dbms { get; }

    /// <summary>방식에 따라 막는 이유. 비어 있으면 쓸 수 있다.</summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함)</param>
    /// <param name="supportsInvisibleColumns">서버가 INVISIBLE 열을 지원하는지(Oracle 12.1 이상)</param>
    IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool supportsInvisibleColumns);

    /// <summary>원래 순서와 새 순서가 처음 달라지는 위치(0부터). 같으면 -1.</summary>
    int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired);

    /// <summary>
    /// 방식 ①: 처음 달라지는 위치부터 새 순서대로 열마다 INVISIBLE 로 바꾼 바로 다음 VISIBLE 로 바꾼다.
    /// 모든 열이 동시에 숨는 순간이 없다. 바뀐 것이 없으면 빈 목록.
    /// </summary>
    /// <param name="original">보이는 열 이름(현재 위치 순)</param>
    /// <param name="desired">새 순서의 열 이름(같은 열의 순열)</param>
    IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired);

    /// <summary>
    /// 방식 ① 문장 전체를 익명 블록 하나로 묶는다. 운영 접속에서 정책 관문의 확인을 문장마다가 아니라 한 번만 받기 위함이다
    /// (맨 윗줄 주석에 대상 표·문장 수가 보여 확인 대화상자에 그대로 뜬다). 문장은 순서대로 돌고 처음 오류에서 멈춘다.
    /// </summary>
    SqlStatement AsSingleBlock(DbObjectRef table, IReadOnlyList<SqlStatement> statements);

    /// <summary>방식 ① 문장을 <c>;</c> 로 끝나는 텍스트로(미리보기용).</summary>
    string ToText(IReadOnlyList<SqlStatement> statements);

    /// <summary>
    /// 방식 ②: 재생성 스크립트(자동으로 실행하지 않는다). 머리에 「검토 뒤 실행」 주석.
    /// </summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함, 현재 순서)</param>
    /// <param name="desired">보이는 열의 새 순서(INVISIBLE 열은 뒤에 붙는다)</param>
    /// <param name="enterprise">Enterprise Edition 이면 온라인 재정의(Oracle 은 DBMS_REDEFINITION), 아니면 CTAS + RENAME</param>
    /// <param name="usePrimaryKey">기본키가 있으면 true(CONS_USE_PK), 없으면 ROWID 로 재정의</param>
    /// <param name="dependentDdl">제약·인덱스·트리거·권한·주석 DDL(<c>meta.ddl</c> 의 종속 객체 부분). 없으면 빈 문자열</param>
    string Rebuild(
        DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string dependentDdl);

    /// <summary>
    /// 표 DDL(종속 객체 없음)과 종속 객체까지 담은 DDL 에서 종속 객체 부분만 떼어 낸다.
    /// <c>meta.ddl</c> 은 표 DDL 뒤에 종속 DDL 을 이어 붙이므로 앞부분이 같으면 뒤를 돌려주고, 아니면 전체를 돌려준다.
    /// </summary>
    string DependentPart(string tableDdl, string fullDdl);

    /// <summary>표를 참조하는 소스 줄 가운데 열 순서에 민감한 위험 후보만 남긴다(한 줄 단위 판정이라 줄을 넘는 문장은 못 찾는다).</summary>
    IReadOnlyList<ColumnReorderRiskHit> FilterRiskHits(IEnumerable<SourceSearchHit> lines, DbObjectRef table);
}
