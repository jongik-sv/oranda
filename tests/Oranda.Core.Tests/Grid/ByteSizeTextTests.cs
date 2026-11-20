using Oranda.Core.Grid;

namespace Oranda.Core.Tests.Grid;

/// <summary>용량(바이트) 표시: 1024 단위 K·M·G·T, 소수 1자리(SU-1).</summary>
public sealed class ByteSizeTextTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 K")]
    [InlineData(1536L, "1.5 K")]
    [InlineData(12_897_485L, "12.3 M")]
    [InlineData(1L << 30, "1.0 G")]
    [InlineData(2_638_827_906_662L, "2.4 T")]
    public void Format_UsesBinaryUnitsWithOneDecimal(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeText.Format(bytes));
    }

    [Theory]
    [InlineData(1_048_575L, "1.0 M")] // 1023.999 K 를 반올림하면 1024.0 K → 다음 단위로 올린다
    [InlineData(1_048_524L, "1023.9 K")]
    [InlineData(1L << 50, "1024.0 T")] // T 보다 큰 단위는 없다
    public void Format_PromotesWhenRoundingReachesNextUnit(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeText.Format(bytes));
    }

    [Fact]
    public void Format_NullIsEmptyAndNegativeKeepsSign()
    {
        Assert.Equal(string.Empty, ByteSizeText.Format(null));
        Assert.Equal("-1.5 K", ByteSizeText.Format(-1536));
        Assert.Equal("-512 B", ByteSizeText.Format(-512));
        Assert.Equal("-8388608.0 T", ByteSizeText.Format(long.MinValue)); // 절댓값 넘침 없이 글을 만든다
    }

    [Fact]
    public void Exact_ShowsGroupedBytes()
    {
        Assert.Equal("12,897,485 B", ByteSizeText.Exact(12_897_485));
        Assert.Equal(string.Empty, ByteSizeText.Exact(null));
    }

    [Theory]
    [InlineData(1536, "1.5 K")]
    [InlineData(1536L, "1.5 K")]
    [InlineData(1536.0, "1.5 K")]
    public void FromValue_ReadsAnyIntegerLikeNumber(object value, string expected)
    {
        Assert.Equal(expected, ByteSizeText.FromValue(value));
    }

    [Fact]
    public void FromValue_NonNumberIsNull()
    {
        Assert.Null(ByteSizeText.FromValue("합계"));
        Assert.Null(ByteSizeText.FromValue(null));
        Assert.Equal("1.5 K", ByteSizeText.FromValue(1536m));
    }
}
