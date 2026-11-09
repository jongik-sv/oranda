using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// Tibero DDL 일괄 추출. 흐름은 Core <see cref="DdlBulkExport"/> 이고 DDL 은 <see cref="TiberoMetadataProvider"/> 의 GetDdlAsync 가 만든다.
/// 기본 유형 목록은 Oracle 과 같은 의존 순서에서 Tibero 가 DDL 을 만들 수 있는 유형만 남긴 것이다(구체화 뷰 = 아직 DDL 없음, Java 저장 객체 = Tibero 에 없음).
/// </summary>
[OrandaService(typeof(IDdlBulkExporter), Dbms = Dbms.Tibero)]
internal sealed class TiberoDdlBulkExporter : IDdlBulkExporter
{
    /// <summary>의존 순서(앞 유형이 먼저 만들어져야 하는 순). 파일에도 이 순서로 쓴다.</summary>
    internal static readonly IReadOnlyList<string> Types =
    [
        "SEQUENCE", "TYPE", "TABLE", "VIEW", "SYNONYM", "FUNCTION", "PROCEDURE",
        "PACKAGE", "PACKAGE BODY", "TYPE BODY", "TRIGGER",
    ];

    public Dbms Dbms => Dbms.Tibero;

    public IReadOnlyList<string> DefaultTypes => Types;

    public Task<DdlExtractResult> ExtractAsync(IMetadataProvider meta, ISession session, DdlExtractRequest request,
                                               IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
        DdlBulkExport.ExtractAsync(meta, session, request, Types, progress, ct);
}
