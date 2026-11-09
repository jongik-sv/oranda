using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

public class TiberoDbmsOutputTests
{
    [Fact]
    public void Decode_reads_the_measured_shape()
    {
        // 실측(6.2.15): PUT_LINE('첫 줄'), PUT_LINE(NULL), PUT_LINE('a' || CHR(10) || 'b') 는 서버가 두 줄로 나눈다.
        var lines = TiberoDbmsOutput.Decode("3:첫 줄\n-\n1:a\n1:b\n");
        Assert.Equal(["첫 줄", "", "a", "b"], lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Decode_of_an_empty_buffer_has_no_lines(string? packed)
    {
        Assert.Empty(TiberoDbmsOutput.Decode(packed));
    }

    [Fact]
    public void Decode_keeps_colons_newlines_and_dashes_inside_a_line()
    {
        Assert.Equal(["a:b", "x\ny", "-", " "], TiberoDbmsOutput.Decode("3:a:b\n3:x\ny\n1:-\n1: \n"));
    }

    [Fact]
    public void Decode_counts_characters_outside_the_basic_plane_as_one()
    {
        // 서버 LENGTH 는 글자 수: 😀(UTF-16 두 단위)도 1.
        Assert.Equal(["😀a", "b"], TiberoDbmsOutput.Decode("2:😀a\n1:b\n"));
        // UTF-16 단위로 센 길이도 받는다.
        Assert.Equal(["😀a"], TiberoDbmsOutput.Decode("3:😀a\n"));
    }

    [Fact]
    public void Decode_keeps_a_long_line()
    {
        var line = new string('y', 5000);
        Assert.Equal([line, "L1"], TiberoDbmsOutput.Decode($"5000:{line}\n2:L1\n"));
    }

    [Theory]
    [InlineData("garbage", new[] { "garbage" })]
    [InlineData("2:ok\n9:short\n", new[] { "ok", "9:short" })]
    [InlineData("2:ok\nx:y\n", new[] { "ok", "x:y" })]
    public void Decode_keeps_a_malformed_rest_as_one_line(string packed, string[] expected)
    {
        Assert.Equal(expected, TiberoDbmsOutput.Decode(packed));
    }

    [Fact]
    public void Collect_block_binds_max_n_and_r()
    {
        Assert.Contains(":max", TiberoDbmsOutput.CollectBlock, StringComparison.Ordinal);
        Assert.Contains(":n := n; :r := r;", TiberoDbmsOutput.CollectBlock, StringComparison.Ordinal);
        Assert.Contains("DBMS_OUTPUT.GET_LINES(l, n)", TiberoDbmsOutput.CollectBlock, StringComparison.Ordinal);
    }
}
