using System.Text;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. 데이터 파일(필드·감쌈·레코드 끝 0x1E 0x0A)·LOB 파일·키 곁 파일.
public sealed class LoaderDataWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-loader-writer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly LoaderColumn[] Columns =
    [
        new("ID", "NUMBER(10)"),
        new("NAME", "VARCHAR2(20)"),
        new("D", "DATE"),
        new("DOC", "CLOB"),
        new("PIC", "BLOB"),
    ];

    private string DataText() => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, LoaderDataWriter.DataFileName)));

    [Fact]
    public void Writes_enclosed_fields_empty_nulls_and_the_two_byte_terminator()
    {
        using (var writer = new LoaderDataWriter(_dir, Columns))
        {
            Assert.True(writer.TryWrite([PortableNumber.FromText("1"), "a \"q\", b\r\nc", new DateTime(2026, 1, 2, 3, 4, 5), null, null], ["1"], out var error), error);
            Assert.True(writer.TryWrite([PortableNumber.FromText("2"), null, null, null, null], ["2"], out error), error);
            writer.Complete();
            Assert.Equal(2, writer.Records);
        }

        var bytes = File.ReadAllBytes(Path.Combine(_dir, LoaderDataWriter.DataFileName));
        Assert.NotEqual(0xEF, bytes[0]); // BOM 없음
        Assert.Equal(new byte[] { 0x1E, 0x0A }, bytes[^2..]);
        Assert.Equal(
            "\"1\",\"a \"\"q\"\", b\r\nc\",\"2026-01-02 03:04:05\",,\u001E\n\"2\",,,,\u001E\n",
            DataText());
    }

    [Fact]
    public void Lob_values_go_to_their_own_files_and_the_field_holds_the_relative_name()
    {
        using (var writer = new LoaderDataWriter(_dir, Columns))
        {
            Assert.True(writer.TryWrite([PortableNumber.FromText("1"), null, null, "본문\u001E포함", new byte[] { 1, 2, 3 }], ["1"], out var error), error);
            Assert.True(writer.TryWrite([PortableNumber.FromText("2"), null, null, string.Empty, null], ["2"], out error), error);
            writer.Complete();
            Assert.Equal(3, writer.LobFiles);
        }

        var clob1 = LoaderDataWriter.LobRelativePath(3, 1);
        var blob1 = LoaderDataWriter.LobRelativePath(4, 1);
        var clob2 = LoaderDataWriter.LobRelativePath(3, 2);
        Assert.Equal(Path.Combine("lob", "4", "1.bin"), clob1);
        Assert.Equal($"\"1\",,,\"{clob1}\",\"{blob1}\"\u001E\n\"2\",,,\"{clob2}\",\u001E\n", DataText());
        Assert.Equal("본문\u001E포함", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_dir, clob1))));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_dir, blob1)));
        Assert.Empty(File.ReadAllBytes(Path.Combine(_dir, clob2)));
    }

    [Fact]
    public void A_text_value_with_the_record_separator_fails_that_row_and_writes_nothing()
    {
        using var writer = new LoaderDataWriter(_dir, Columns);
        Assert.False(writer.TryWrite([PortableNumber.FromText("1"), "x\u001Ey", null, "clob", new byte[] { 9 }], ["1"], out var error));
        Assert.Contains("NAME", error, StringComparison.Ordinal);
        Assert.DoesNotContain("x\u001Ey", error, StringComparison.Ordinal);
        Assert.Equal(0, writer.Records);
        Assert.Equal(0, writer.LobFiles);
        Assert.True(writer.TryWrite([PortableNumber.FromText("2"), "ok", null, null, null], ["2"], out error), error);
        writer.Complete();

        Assert.Equal("\"2\",\"ok\",,,\u001E\n", DataText());
        Assert.False(Directory.Exists(Path.Combine(_dir, "lob")));
    }

    [Fact]
    public void A_conversion_failure_fails_the_row()
    {
        using var writer = new LoaderDataWriter(_dir, Columns);
        Assert.False(writer.TryWrite(["not a number", null, null, null, null], ["1"], out var error));
        Assert.Contains("ID", error, StringComparison.Ordinal);
        Assert.False(writer.TryWrite([null, null, null, null, "text into blob"], ["1"], out error));
        Assert.Contains("PIC", error, StringComparison.Ordinal);
        Assert.Equal(0, writer.Records);
    }

    [Fact]
    public void Keys_round_trip_for_rejected_record_numbers()
    {
        using var writer = new LoaderDataWriter(_dir, [new LoaderColumn("ID", "NUMBER")]);
        List<string?[]> keys =
        [
            ["1", "a"],
            ["줄\n바꿈\r\n", "탭\t역\\"],
            [null, string.Empty],
            [],
        ];
        foreach (var key in keys)
        {
            Assert.True(writer.TryWrite([PortableNumber.FromText("1")], key, out var error), error);
        }

        var before = writer.ReadKeys(new HashSet<long> { 2 });
        Assert.Equal(keys[1], before[2]);

        writer.Complete();
        Assert.Equal(4, File.ReadAllLines(writer.KeysFile).Length);
        var read = LoaderDataWriter.ReadKeys(_dir, new HashSet<long> { 1, 2, 3, 4, 99 });
        Assert.Equal(4, read.Count);
        Assert.Equal(keys[0], read[1]);
        Assert.Equal(keys[1], read[2]);
        Assert.Equal(keys[2], read[3]);
        Assert.Empty(read[4]);
    }

    [Fact]
    public void TbLoader_format_escapes_instead_of_doubling()
    {
        using (var writer = new LoaderDataWriter(_dir, [new LoaderColumn("A", "VARCHAR2(10)"), new LoaderColumn("B", "NUMBER")], LoaderFileFormat.TbLoader))
        {
            Assert.True(writer.TryWrite(["a\"b\\c", null], [], out var error), error);
            writer.Complete();
        }

        Assert.Equal("\"a\\\"b\\\\c\",\u001E\n", DataText());
    }

    [Fact]
    public void Unsupported_column_types_are_refused_up_front()
    {
        Assert.Throws<ArgumentException>(() => new LoaderDataWriter(_dir, [new LoaderColumn("L", "LONG")]));
    }
}
