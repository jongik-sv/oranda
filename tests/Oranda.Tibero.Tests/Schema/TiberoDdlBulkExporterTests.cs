using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Tibero.Schema;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>DDL 일괄 추출 Tibero 판: Core 흐름(<see cref="DdlBulkExport"/>)에 Tibero 기본 유형을 넘기는지, Tibero 제공자의 DDL 이 파일로 나가는지.</summary>
public sealed class TiberoDdlBulkExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-tb-ddl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Default_types_are_the_tibero_list_in_dependency_order_and_the_list_comes_from_the_provider()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var meta = new TiberoMetadataProvider(Recording(calls, _ => Table(["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"])));
        await using var session = Session();
        var exporter = new TiberoDdlBulkExporter();

        var result = await exporter.ExtractAsync(meta, session, new DdlExtractRequest { Owner = "HR", OutputDirectory = _dir }, null, CancellationToken.None);

        Assert.Equal(Oranda.Core.Connection.Dbms.Tibero, exporter.Dbms);
        Assert.DoesNotContain("MATERIALIZED VIEW", exporter.DefaultTypes);
        Assert.Equal(["SEQUENCE", "TYPE", "TABLE", "VIEW", "SYNONYM", "FUNCTION", "PROCEDURE", "PACKAGE", "PACKAGE BODY", "TYPE BODY", "TRIGGER"], exporter.DefaultTypes);
        Assert.Equal("meta.objects", Assert.Single(calls).Id);
        Assert.Empty(result.Items);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Picked_objects_are_written_one_file_per_object_with_tibero_ddl()
    {
        var meta = new TiberoMetadataProvider(Recording([], _ => Table(["TEXT"], ["PROCEDURE p_raise IS\n"], ["BEGIN NULL; END;"])));
        await using var session = Session();
        var request = new DdlExtractRequest
        {
            Owner = "HR",
            OutputDirectory = _dir,
            Objects = [new DbObjectRef("HR", "P_RAISE", "PROCEDURE"), new DbObjectRef("HR", "V1", "MATERIALIZED VIEW")],
            Layout = DdlExtractLayout.FilePerObject,
        };

        var result = await new TiberoDdlBulkExporter().ExtractAsync(meta, session, request, null, CancellationToken.None);

        Assert.Equal([true, false], result.Items.Select(i => i.Succeeded));
        var file = Assert.Single(result.Files);
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE \"HR\".\"P_RAISE\"", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }
}
