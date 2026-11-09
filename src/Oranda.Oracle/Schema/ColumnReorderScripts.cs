using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;

namespace Oranda.Oracle.Schema;

/// <summary>
/// 열 순서 바꾸기 스크립트 만들기. 문장·텍스트만 만들고 실행하지 않는다 — 실행은 반드시 <c>IExecutionService</c>(정책 관문)로 보낸다.
/// 내용은 Core <see cref="ColumnReorderBuilder"/> 가 정본이고(Tibero 와 함께 쓴다), 여기는 Oracle 의 공개 표면을 그대로 두고 호출만 넘긴다.
/// 소유: TSK-13-01.
/// </summary>
public static class ColumnReorderScripts
{
    /// <summary>방식에 따라 막는 이유. 비어 있으면 쓸 수 있다.</summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함)</param>
    /// <param name="atLeast12c">서버가 12c 이상인지(INVISIBLE 열은 12.1 부터)</param>
    public static IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool atLeast12c) =>
        ColumnReorderBuilder.BlockReasons(method, columns, traits, atLeast12c);

    /// <summary>원래 순서와 새 순서가 처음 달라지는 위치(0부터). 같으면 -1.</summary>
    public static int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        ColumnReorderBuilder.FirstDifference(original, desired);

    /// <summary>방식 ①: 처음 달라지는 위치부터 열마다 <c>MODIFY (c INVISIBLE)</c> 바로 다음 <c>MODIFY (c VISIBLE)</c>. 바뀐 것이 없으면 빈 목록.</summary>
    public static IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        ColumnReorderBuilder.InvisibleVisible(table, original, desired);

    /// <summary>방식 ① 문장 전체를 익명 PL/SQL 블록 하나로 묶는다(정책 관문의 확인을 한 번만 받기 위함).</summary>
    public static SqlStatement AsSingleBlock(DbObjectRef table, IReadOnlyList<SqlStatement> statements) =>
        ColumnReorderBuilder.AsSingleBlock(table, statements);

    /// <summary>방식 ① 문장을 <c>;</c> 로 끝나는 텍스트로(미리보기용).</summary>
    public static string ToText(IReadOnlyList<SqlStatement> statements) => ColumnReorderBuilder.ToText(statements);

    /// <summary>방식 ②: 재생성 스크립트(자동으로 실행하지 않는다). Enterprise Edition 이면 DBMS_REDEFINITION, 아니면 CTAS + RENAME.</summary>
    /// <param name="columns">표의 모든 열(INVISIBLE 포함, 현재 순서)</param>
    /// <param name="desired">보이는 열의 새 순서(INVISIBLE 열은 뒤에 붙는다)</param>
    /// <param name="enterprise">Enterprise Edition 이면 DBMS_REDEFINITION, 아니면 CTAS + RENAME</param>
    /// <param name="usePrimaryKey">기본키가 있으면 true(CONS_USE_PK), 없으면 ROWID 로 재정의</param>
    /// <param name="dependentDdl">제약·인덱스·트리거·권한·주석 DDL(<c>meta.ddl</c> 의 종속 객체 부분). 없으면 빈 문자열</param>
    public static string Rebuild(
        DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string dependentDdl) =>
        ColumnReorderBuilder.Rebuild(table, columns, desired, enterprise, usePrimaryKey, dependentDdl);

    /// <summary>표 DDL(종속 객체 없음)과 종속 객체까지 담은 DDL 에서 종속 객체 부분만 떼어 낸다.</summary>
    public static string DependentPart(string tableDdl, string fullDdl) => ColumnReorderBuilder.DependentPart(tableDdl, fullDdl);
}
