using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

// O4: 정적 도우미(SchemaStatements·ColumnReorderScripts·DdlBulkExporter)를 Core 인터페이스로 감싼다. 내용은 정적 도우미가 정본이고
// 여기는 호출만 넘긴다 — 정적 도우미를 쓰는 Oracle 시험은 그대로 둔다.

/// <inheritdoc cref="ISchemaStatements"/>
[OrandaService(typeof(ISchemaStatements), Dbms = Dbms.Oracle)]
internal sealed class OracleSchemaStatements : ISchemaStatements
{
    public Dbms Dbms => Dbms.Oracle;

    public SqlStatement? Compile(DbObjectRef obj) => SchemaStatements.Compile(obj);

    public SqlStatement PurgeItem(RecycleBinItem item) => SchemaStatements.PurgeItem(item);

    public SqlStatement PurgeAll() => SchemaStatements.PurgeAll();

    public SqlStatement Flashback(RecycleBinItem item, string? renameTo = null) => SchemaStatements.Flashback(item, renameTo);

    public string ParameterChangeDdl(string name, string value, ParameterScope scope) => SchemaStatements.ParameterChangeDdl(name, value, scope);
}

/// <inheritdoc cref="IColumnReorderScripts"/>
[OrandaService(typeof(IColumnReorderScripts), Dbms = Dbms.Oracle)]
internal sealed class OracleColumnReorderScripts : IColumnReorderScripts
{
    public Dbms Dbms => Dbms.Oracle;

    public IReadOnlyList<string> BlockReasons(
        ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool supportsInvisibleColumns) =>
        ColumnReorderScripts.BlockReasons(method, columns, traits, supportsInvisibleColumns);

    public int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        ColumnReorderScripts.FirstDifference(original, desired);

    public IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
        ColumnReorderScripts.InvisibleVisible(table, original, desired);

    public SqlStatement AsSingleBlock(DbObjectRef table, IReadOnlyList<SqlStatement> statements) =>
        ColumnReorderScripts.AsSingleBlock(table, statements);

    public string ToText(IReadOnlyList<SqlStatement> statements) => ColumnReorderScripts.ToText(statements);

    public string Rebuild(
        DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string dependentDdl) =>
        ColumnReorderScripts.Rebuild(table, columns, desired, enterprise, usePrimaryKey, dependentDdl);

    public string DependentPart(string tableDdl, string fullDdl) => ColumnReorderScripts.DependentPart(tableDdl, fullDdl);

    public IReadOnlyList<ColumnReorderRiskHit> FilterRiskHits(IEnumerable<SourceSearchHit> lines, DbObjectRef table) =>
        ColumnReorderSourceScan.Filter(lines, table);
}

/// <inheritdoc cref="IDdlBulkExporter"/>
[OrandaService(typeof(IDdlBulkExporter), Dbms = Dbms.Oracle)]
internal sealed class OracleDdlBulkExporter : IDdlBulkExporter
{
    public Dbms Dbms => Dbms.Oracle;

    public IReadOnlyList<string> DefaultTypes => DdlBulkExporter.DefaultTypes;

    public Task<DdlExtractResult> ExtractAsync(IMetadataProvider meta, ISession session, DdlExtractRequest request,
                                               IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
        DdlBulkExporter.ExtractAsync(meta, session, request, progress, ct);
}
