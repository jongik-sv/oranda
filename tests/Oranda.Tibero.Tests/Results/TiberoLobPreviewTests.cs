using Oranda.Core.Grid;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

public class TiberoLobPreviewTests
{
    private static Func<char[], int, int, int> CharReader(string text, int maxPerRead = int.MaxValue)
    {
        var pos = 0;
        return (buf, off, count) =>
        {
            var n = Math.Min(Math.Min(count, maxPerRead), text.Length - pos);
            text.CopyTo(pos, buf, off, n);
            pos += n;
            return n;
        };
    }

    private static Func<byte[], int, int, int> ByteReader(byte[] data, int maxPerRead = int.MaxValue)
    {
        var pos = 0;
        return (buf, off, count) =>
        {
            var n = Math.Min(Math.Min(count, maxPerRead), data.Length - pos);
            Array.Copy(data, pos, buf, off, n);
            pos += n;
            return n;
        };
    }

    [Fact]
    public void Clob_length_is_ucs2_bytes_halved_and_preview_is_cut_to_size()
    {
        var lob = TiberoLobPreview.Clob(false, 20, CharReader("0123456789"), "loc", 4);

        Assert.Equal(LobKind.Clob, lob.Kind);
        Assert.Equal(10, lob.Length);
        Assert.Equal("0123", lob.TextPreview);
        Assert.True(lob.IsTruncated);
        Assert.Equal("loc", lob.Locator);
    }

    [Fact]
    public void Clob_that_fits_is_not_truncated_and_short_reads_are_filled()
    {
        var lob = TiberoLobPreview.Clob(false, 10, CharReader("hello", maxPerRead: 2), "loc", 4000);

        Assert.Equal(5, lob.Length);
        Assert.Equal("hello", lob.TextPreview);
        Assert.False(lob.IsTruncated);
    }

    [Fact]
    public void Nclob_keeps_kind()
    {
        var lob = TiberoLobPreview.Clob(true, 4, CharReader("한글"), "loc", 10);

        Assert.Equal(LobKind.NClob, lob.Kind);
        Assert.Equal("한글", lob.TextPreview);
    }

    [Fact]
    public void Preview_does_not_end_on_half_a_surrogate_pair()
    {
        var text = "ab\U0001F600cd"; // 6 UTF-16 글자
        var lob = TiberoLobPreview.Clob(false, text.Length * 2, CharReader(text), "loc", 3);

        Assert.Equal("ab", lob.TextPreview);
        Assert.True(lob.IsTruncated);
        Assert.Equal(6, lob.Length);
    }

    [Fact]
    public void Temp_nclob_with_zero_length_but_short_content_gets_real_length()
    {
        var lob = TiberoLobPreview.Clob(true, 0, CharReader("abc"), "loc", 4000);

        Assert.Equal(3, lob.Length);
        Assert.Equal("abc", lob.TextPreview);
        Assert.False(lob.IsTruncated);
    }

    [Fact]
    public void Temp_nclob_with_zero_length_and_full_preview_is_unknown_length_and_truncated()
    {
        var lob = TiberoLobPreview.Clob(true, 0, CharReader("abcdef"), "loc", 4);

        Assert.Equal(TiberoLobPreview.UnknownLength, lob.Length);
        Assert.Equal("abcd", lob.TextPreview);
        Assert.True(lob.IsTruncated);
    }

    [Fact]
    public void Empty_clob_is_empty_and_not_truncated()
    {
        var lob = TiberoLobPreview.Clob(false, 0, CharReader(string.Empty), "loc", 4000);

        Assert.Equal(0, lob.Length);
        Assert.Equal(string.Empty, lob.TextPreview);
        Assert.False(lob.IsTruncated);
    }

    [Fact]
    public void Zero_preview_size_reads_nothing_but_reports_truncated_when_content_exists()
    {
        var lob = TiberoLobPreview.Clob(false, 10, CharReader("hello"), "loc", 0);

        Assert.Equal(string.Empty, lob.TextPreview);
        Assert.Equal(5, lob.Length);
        Assert.True(lob.IsTruncated);
    }

    [Fact]
    public void Blob_preview_is_cut_and_marked_truncated()
    {
        var data = Enumerable.Range(0, 10).Select(i => (byte)i).ToArray();
        var lob = TiberoLobPreview.Blob(10, ByteReader(data, maxPerRead: 3), "loc", 5);

        Assert.Equal(LobKind.Blob, lob.Kind);
        Assert.Equal(10, lob.Length);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, lob.BinaryPreview.ToArray());
        Assert.True(lob.IsTruncated);
    }

    [Fact]
    public void Blob_that_fits_is_complete()
    {
        var lob = TiberoLobPreview.Blob(3, ByteReader(new byte[] { 7, 8, 9 }), "loc", 4000);

        Assert.Equal(new byte[] { 7, 8, 9 }, lob.BinaryPreview.ToArray());
        Assert.False(lob.IsTruncated);
    }
}
