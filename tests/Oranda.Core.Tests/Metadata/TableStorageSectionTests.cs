using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>객체 상세 「저장」 구역(설계 18.2): 지금 저장 속성과 LOB 저장 절을 항목·값으로 보인다. 모르는 값은 「알 수 없음」.</summary>
public class TableStorageSectionTests
{
    private static readonly DbObjectRef T = new("SAMPLE", "ORDERS", "TABLE");

    private static TableInfo Table(TableStorage? storage, params LobInfo[] lobs) =>
        new(T, [new ColumnInfo("ID", 1, "NUMBER")]) { Tablespace = "USERS", Storage = storage, Lobs = lobs };

    private static Dictionary<string, object?> Values(DetailSection s) => s.Rows.ToDictionary(r => (string)r[0]!, r => r[1]);

    [Fact]
    public void Table_storage_values_are_shown_as_rows()
    {
        var section = TableStorageSection.Build(Table(new TableStorage
        {
            PctFree = 10, InitTrans = 1, Compression = TableCompression.Basic, Logging = true, Parallel = 1,
        }))!;

        Assert.Equal("저장", section.Title);
        var v = Values(section);
        Assert.Equal("USERS", v["테이블스페이스"]);
        Assert.Equal("10", v["PCTFREE"]);
        Assert.Equal("1", v["INITRANS"]);
        Assert.Equal("BASIC", v["압축"]);
        Assert.Equal("예", v["LOGGING"]);
        Assert.Equal("NOPARALLEL", v["병렬"]);
    }

    [Fact]
    public void Unknown_values_say_so_and_parallel_degrees_read_as_clauses()
    {
        var v = Values(TableStorageSection.Build(Table(new TableStorage { Parallel = 4 }))!);
        Assert.Equal("알 수 없음", v["PCTFREE"]);
        Assert.Equal("알 수 없음", v["압축"]);
        Assert.Equal("PARALLEL 4", v["병렬"]);
        Assert.Equal("PARALLEL(기본)", Values(TableStorageSection.Build(Table(new TableStorage { Parallel = 0 }))!)["병렬"]);
    }

    [Fact]
    public void Lob_columns_get_one_row_each_with_only_known_parts()
    {
        var v = Values(TableStorageSection.Build(Table(new TableStorage(),
            new LobInfo("MEMO", new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "LOB01", Compression = LobCompression.Medium, Deduplicate = false, InRow = true }),
            new LobInfo("DOC", new LobStorage { Tablespace = "LOB02" }),
            new LobInfo("RAW", new LobStorage())))!);

        Assert.Equal("SECUREFILE · LOB01 · 압축 MEDIUM · 중복 유지 · 행 안에", v["LOB MEMO"]);
        Assert.Equal("LOB02", v["LOB DOC"]);
        Assert.Equal("알 수 없음", v["LOB RAW"]);
    }

    [Fact]
    public void Tables_without_storage_details_get_no_section()
    {
        Assert.Null(TableStorageSection.Build(Table(null)));
    }
}
