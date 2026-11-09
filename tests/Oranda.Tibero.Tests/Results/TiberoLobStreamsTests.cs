using System.Data;
using System.Data.Common;
using System.Text;
using Oranda.Core.Grid;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Results;

[Collection("FakeDriver")]
public class TiberoLobStreamsTests
{
    private static TiberoDriver Driver => FakeDriverHost.Driver;

    private static DbDataReader ReaderOf(object value, Type type)
    {
        var table = new DataTable();
        table.Columns.Add("c", type);
        table.Rows.Add(value);
        var reader = FakeDriverHost.NewReader(table);
        Assert.True(reader.Read());
        return reader;
    }

    private static LobValue ClobCell(string text, int preview = 4)
    {
        using var reader = ReaderOf(text, typeof(string));
        return (LobValue)TiberoValues.ReadCell(Driver, reader, 0, ColumnDataKind.Clob, TiberoColumnTypes.PtClob, preview)!;
    }

    private static LobValue BlobCell(byte[] data, int preview = 4)
    {
        using var reader = ReaderOf(data, typeof(byte[]));
        return (LobValue)TiberoValues.ReadCell(Driver, reader, 0, ColumnDataKind.Blob, TiberoColumnTypes.PtBlob, preview)!;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Clob_is_read_as_utf8_from_the_start_whatever_the_preview_position()
    {
        var text = "가나다abc\U0001F600끝";
        var lob = ClobCell(text);

        using var stream = TiberoLobStreams.Open(lob);

        Assert.Equal(Encoding.UTF8.GetBytes(text), ReadAll(stream));
    }

    [Fact]
    public void Large_clob_crosses_chunk_boundaries_without_breaking_surrogate_pairs()
    {
        var text = string.Concat(Enumerable.Repeat("a\U0001F600", ClobUtf8StreamChunk));
        var lob = ClobCell(text);

        using var stream = TiberoLobStreams.Open(lob);

        Assert.Equal(text, Encoding.UTF8.GetString(ReadAll(stream)));
    }

    private const int ClobUtf8StreamChunk = 40_000; // 조각(32K 문자)보다 크게

    [Fact]
    public void Blob_is_read_completely_and_independently_of_the_cell_locator()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var lob = BlobCell(data);

        using var first = TiberoLobStreams.Open(lob);
        var partial = new byte[10];
        Assert.Equal(10, first.Read(partial, 0, 10));
        using var second = TiberoLobStreams.Open(lob);

        Assert.Equal(data, ReadAll(second));
        Assert.Equal(data[..10], partial);
        Assert.Equal(1000, first.Length);
    }

    [Fact]
    public void Opening_does_not_move_the_cell_locator()
    {
        var lob = ClobCell("abcdef");
        var locator = Assert.IsType<TiberoLob>(lob.Locator);
        var before = locator.Position;

        using (var stream = TiberoLobStreams.Open(lob))
        {
            ReadAll(stream);
        }

        Assert.Equal(before, locator.Position);
        Assert.Equal(12, locator.Length); // 원본은 닫히지 않았다
    }

    [Fact]
    public void Closing_the_stream_closes_only_the_copy()
    {
        var lob = BlobCell([1, 2, 3]);
        var locator = Assert.IsType<TiberoLob>(lob.Locator);

        TiberoLobStreams.Open(lob).Dispose();

        var buffer = new byte[3];
        locator.Position = 0;
        Assert.Equal(3, locator.Read(buffer, 0, 3));
    }

    [Fact]
    public void Missing_locator_asks_for_a_rerun()
    {
        var preview = new LobValue(LobKind.Clob, 5) { TextPreview = "hello" };

        var ex = Assert.Throws<InvalidOperationException>(() => TiberoLobStreams.Open(preview));

        Assert.Contains("다시 실행", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => TiberoLobStreams.Open(new LobValue(LobKind.Clob, 5) { Locator = "not a lob" }));
    }
}
