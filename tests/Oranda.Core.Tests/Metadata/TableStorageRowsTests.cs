using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>공급자 저장 속성 읽기 규칙(T13c, <see cref="TableStorageRows"/>): Oracle·Tibero 가 같이 쓴다.</summary>
public class TableStorageRowsTests
{
    [Theory]
    [InlineData("         1", 1)]
    [InlineData("   DEFAULT", 0)]
    [InlineData("default", 0)]
    [InlineData("8", 8)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("-3", null)]
    [InlineData("MANY", null)]
    public void Oracle_degree_is_trimmed_and_default_means_server_parallel(string? degree, int? expected) =>
        Assert.Equal(expected, TableStorageRows.Parallel(degree));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(-2, null)]
    public void Tibero_degree_zero_is_noparallel_and_minus_one_is_server_parallel(int degree, int? expected) =>
        Assert.Equal(expected, TableStorageRows.TiberoParallel((decimal)degree));

    [Theory]
    [InlineData("DISABLED", null, TableCompression.None)]
    [InlineData("ENABLED", "BASIC", TableCompression.Basic)]
    [InlineData("ENABLED", "DIRECT LOAD ONLY", TableCompression.Basic)]
    [InlineData("ENABLED", "ADVANCED", TableCompression.Advanced)]
    [InlineData("ENABLED", "OLTP", TableCompression.Advanced)]
    [InlineData("ENABLED", "FOR ALL OPERATIONS", TableCompression.Advanced)]
    [InlineData("ENABLED", "QUERY LOW", TableCompression.Other)]
    [InlineData("ENABLED", null, TableCompression.Other)]
    [InlineData("YES", null, TableCompression.Basic)]
    [InlineData("NO", null, TableCompression.None)]
    [InlineData(null, null, null)]
    public void Compression_follows_compression_and_compress_for(string? compression, string? compressFor, TableCompression? expected) =>
        Assert.Equal(expected, TableStorageRows.Compression(compression, compressFor));

    [Fact]
    public void Table_reads_numbers_and_flags_and_leaves_unknown_values_null()
    {
        Assert.Equal(
            new TableStorage { PctFree = 10, InitTrans = 1, Compression = TableCompression.None, Logging = true, Parallel = 1 },
            TableStorageRows.Table(10m, 1m, "DISABLED", null, "YES", "1"));
        Assert.Equal(new TableStorage(), TableStorageRows.Table(null, null, null, null, null, null));
        Assert.Null(TableStorageRows.Table(null, null, null, null, "MAYBE", null).Logging);

        // 숫자가 아닌 글·소수는 null(테이블 읽기 전체가 실패하지 않게).
        var odd = TableStorageRows.Table("DEFAULT", 2.5m, null, null, null, null);
        Assert.Equal(((int?)null, (int?)null), (odd.PctFree, odd.InitTrans));
        Assert.Equal(20, TableStorageRows.Table("20", null, null, null, null, null).PctFree);
        Assert.Null(TableStorageRows.TiberoParallel("DEFAULT"));
    }

    /// <summary>IOT·외부 테이블은 테이블 줄 값이 실제 값이 아니라 병렬만 남긴다.</summary>
    [Theory]
    [InlineData(TableKind.Iot, true)]
    [InlineData(TableKind.External, true)]
    [InlineData(TableKind.Heap, false)]
    [InlineData(TableKind.Temporary, false)]
    public void Iot_and_external_tables_keep_only_the_parallel_degree(TableKind kind, bool onlyParallel)
    {
        var read = new TableStorage { PctFree = 0, InitTrans = 0, Compression = TableCompression.None, Logging = true, Parallel = 4 };

        Assert.Equal(onlyParallel ? new TableStorage { Parallel = 4 } : read, TableStorageRows.ForKind(read, kind));
    }

    [Fact]
    public void Lob_maps_securefile_compression_deduplication_and_in_row()
    {
        Assert.Equal(
            new LobInfo("MEMO", new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "LOB01", Compression = LobCompression.High, Deduplicate = true, InRow = false }),
            TableStorageRows.Lob("MEMO", "YES", "LOB01", "HIGH", "LOB", "NO"));
        Assert.Equal(
            new LobInfo("DOC", new LobStorage { Kind = LobStorageKind.BasicFile, InRow = true }),
            TableStorageRows.Lob("DOC", "NO", null, "NONE", "NONE", "YES"));

        // Tibero: SECUREFILE 열이 없고(모름), 압축 없음은 NOCOMPRESS.
        Assert.Equal(
            new LobInfo("PIC", new LobStorage { Tablespace = "TS", Compression = LobCompression.None, Deduplicate = false, InRow = true }),
            TableStorageRows.Lob("PIC", null, "TS", "NOCOMPRESS", "NO", "YES"));
    }
}
