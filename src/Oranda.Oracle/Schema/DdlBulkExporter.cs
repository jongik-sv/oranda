using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

/// <summary>
/// Oracle DDL 일괄 추출(SCH-10). 흐름·파일 쓰기·이름 바꾸기는 Core <see cref="DdlBulkExport"/> 에 있고, 여기서는 Oracle 기본 유형 목록만 정한다.
/// </summary>
public static class DdlBulkExporter
{
    /// <summary>의존 순서(앞 유형이 먼저 만들어져야 하는 순). 파일에도 이 순서로 쓴다.</summary>
    public static IReadOnlyList<string> DefaultTypes { get; } =
    [
        "SEQUENCE", "TYPE", "TABLE", "VIEW", "MATERIALIZED VIEW", "SYNONYM", "FUNCTION", "PROCEDURE",
        "PACKAGE", "PACKAGE BODY", "TYPE BODY", "TRIGGER",
    ];

    public static Task<DdlExtractResult> ExtractAsync(IMetadataProvider meta, ISession session, DdlExtractRequest request,
                                                      IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
        DdlBulkExport.ExtractAsync(meta, session, request, DefaultTypes, progress, ct);

    /// <inheritdoc cref="DdlBulkExport.ApplyRenames"/>
    public static string ApplyRenames(string ddl, IReadOnlyList<DdlRename> renames) => DdlBulkExport.ApplyRenames(ddl, renames);

    /// <inheritdoc cref="DdlBulkExport.SafeFileName"/>
    public static string SafeFileName(string name) => DdlBulkExport.SafeFileName(name);
}
