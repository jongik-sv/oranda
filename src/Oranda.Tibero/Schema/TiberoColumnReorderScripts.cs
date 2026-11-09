using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 열 순서 바꾸기 스크립트 만들기(Tibero) — Oracle <c>OracleColumnReorderScripts</c> 대응(D4). 문장·텍스트만 만들고 실행하지 않는다.
/// 텍스트는 Oracle 과 같은 Core <see cref="ColumnReorderBuilder"/>·<see cref="ColumnReorderSourceFilter"/> 를 쓴다.
/// Oracle 과 달라진 점:
/// <list type="bullet">
/// <item>쓸 수 있는 방식은 다시 만들기(<see cref="ColumnReorderMethod.Rebuild"/>, CTAS + RENAME)뿐이다. INVISIBLE → VISIBLE 방식은 Tibero 의 INVISIBLE 열 DDL 을 실측하지 못해 막는다
/// (<c>Release12Columns</c> 꺼짐, docs/design/tibero.md).</item>
/// <item>온라인 재정의(DBMS_REDEFINITION)는 Oracle 전용이라 <c>enterprise</c> 인자와 상관없이 늘 CTAS + RENAME 으로 만든다.</item>
/// <item><see cref="AsSingleBlock"/> 의 익명 블록(<c>BEGIN … EXECUTE IMMEDIATE '…'; … END;</c>)은 Tibero PSM 도 같은 문법이다. 다만 방식 ① 이 막혀 있어 지금은 부르는 곳이 없다.</item>
/// </list>
/// </summary>
[OrandaService(typeof(IColumnReorderScripts), Dbms = Dbms.Tibero)]
internal sealed class TiberoColumnReorderScripts : IColumnReorderScripts
{
    /// <summary>
    /// 다시 만들기 스크립트를 Tibero 서버로 실측했는가. 다시 만들기는 <c>DependentPart</c> 로 표의 종속 객체 DDL(제약·인덱스·트리거·권한·주석, D2b)을 떼어
    /// CTAS 로 만든 새 표에 다시 붙이고 <c>ALTER TABLE … RENAME TO</c>·<c>MODIFY (… DEFAULT …)</c>·가상 열 <c>ADD (… AS (…))</c> 를 쓴다.
    /// 이 문장들과 종속 객체 옮기기는 Tibero 6 서버에서 <c>TiberoColumnReorderRebuildIt</c>(D4b)로 실측했다(표 정의를 잃을 수 있는 작업이라 실측 전에는 막았다).
    /// </summary>
    internal const bool DependentDdlReady = true;

    internal const string InvisibleNotSupported = "Tibero 는 INVISIBLE 열 DDL 을 아직 확인하지 못해 이 방식을 쓰지 않는다. 다시 만들기(방식 ②)를 쓴다";

    internal const string DependentDdlNotReady = "Tibero 의 다시 만들기 스크립트(CTAS·RENAME·종속 객체 다시 붙이기)를 아직 서버로 확인하지 않아 막는다: 인덱스·제약·트리거·권한·주석을 잃을 수 있다";

    public Dbms Dbms => Dbms.Tibero;

    public IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool supportsInvisibleColumns) =>
        BlockReasons(method, columns, traits, supportsInvisibleColumns, DependentDdlReady);

    /// <summary>시험용: 다시 만들기 실측 여부를 인자로 받는다(막을 때·풀 때를 둘 다 시험한다).</summary>
    internal static IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool supportsInvisibleColumns, bool dependentDdlReady)
    {
        if (method == ColumnReorderMethod.InvisibleVisible)
        {
            return [InvisibleNotSupported];
        }

        var reasons = new List<string>(ColumnReorderBuilder.BlockReasons(method, columns, traits, supportsInvisibleColumns));
        if (!dependentDdlReady)
        {
            reasons.Add(DependentDdlNotReady);
        }

        return reasons;
    }

    public int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        ColumnReorderBuilder.FirstDifference(original, desired);

    public IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        throw new NotSupportedException(InvisibleNotSupported);

    public SqlStatement AsSingleBlock(DbObjectRef table, IReadOnlyList<SqlStatement> statements) =>
        ColumnReorderBuilder.AsSingleBlock(table, statements);

    public string ToText(IReadOnlyList<SqlStatement> statements) => ColumnReorderBuilder.ToText(statements);

    public string Rebuild(
        DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string dependentDdl) =>
        ColumnReorderBuilder.Rebuild(table, columns, desired, enterprise: false, usePrimaryKey, dependentDdl);

    public string DependentPart(string tableDdl, string fullDdl) => ColumnReorderBuilder.DependentPart(tableDdl, fullDdl);

    public IReadOnlyList<ColumnReorderRiskHit> FilterRiskHits(IEnumerable<SourceSearchHit> lines, DbObjectRef table) =>
        ColumnReorderSourceFilter.Filter(lines, table);
}
