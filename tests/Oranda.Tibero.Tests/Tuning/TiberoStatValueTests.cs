using Oranda.Tibero.Tuning;

namespace Oranda.Tibero.Tests.Tuning;

// Tibero 열 통계 글 풀이. 견본은 Tibero 6(build 302172) 시험 DB 의 ORT_CS_ 시험 테이블 실측 글이다.
public class TiberoStatValueTests
{
    [Theory]
    [InlineData("NUMBER", " -3.4113000000000000000000000000000000000E+02", "-341.13")]
    [InlineData("NUMBER", "  9.0000000000000000000000000000000000000E+02", "900")]
    [InlineData("NUMBER", " -3.3976000000000000000000000000000000000E+02", "-339.76")]
    [InlineData("NUMBER", "  0.0000000000000000000000000000000000000E+00", "0")]
    [InlineData("NUMBER", "  1.2500000000000000000000000000000000000E-03", "0.00125")]
    [InlineData("FLOAT", "  1.0000000000000000000000000000000000000E+100", "1E+100")]
    [InlineData("DATE", "20200103140000", "2020-01-03 14:00:00")]
    [InlineData("DATE", "20200106050000", "2020-01-06 05:00:00")]
    [InlineData("DATE", "20200106000000", "2020-01-06")]
    [InlineData("TIMESTAMP(6)", "20230615093001623456000", "2023-06-15 09:30:01.623456")]
    [InlineData("TIMESTAMP(6)", "20230615093001000000000", "2023-06-15 09:30:01")]
    [InlineData("VARCHAR2", "V00001", "V00001")]
    [InlineData("VARCHAR", "가나다0", "가나다0")]
    [InlineData("CHAR", "AA", "AA")]
    [InlineData("NUMBER", "not a number", "not a number")] // 모르는 모양은 그대로
    [InlineData("DATE", "2020-01-03", "2020-01-03")]
    [InlineData("DATE", "20201303140000", "20201303140000")] // 13월 → 그대로
    public void Text_values_follow_the_data_type(string type, string value, string expected) =>
        Assert.Equal(expected, TiberoStatValue.Decode(type, value));

    [Fact]
    public void Missing_values_are_null()
    {
        Assert.Null(TiberoStatValue.Decode("NVARCHAR2", null));
        Assert.Null(TiberoStatValue.Decode("NUMBER", DBNull.Value));
        Assert.Null(TiberoStatValue.Decode("NUMBER", string.Empty));
    }

    [Fact]
    public void Histogram_uses_the_actual_value_and_never_the_character_endpoint_number()
    {
        Assert.Equal("P1", TiberoStatValue.HistogramEndpoint("VARCHAR2", "P1", "65.25390625"));
        Assert.Equal("2020-01-06 05:00:00", TiberoStatValue.HistogramEndpoint("DATE", "20200106050000", "2458855.20833333"));
        Assert.Equal("-339.76", TiberoStatValue.HistogramEndpoint("NUMBER", " -3.3976000000000000000000000000000000000E+02", "-339.76"));
        Assert.Null(TiberoStatValue.HistogramEndpoint("VARCHAR2", null, "65.25390625")); // 문자 열의 endpoint_value 는 뜻 없는 수
        Assert.Equal("12", TiberoStatValue.HistogramEndpoint("NUMBER", null, "12"));
        Assert.Null(TiberoStatValue.HistogramEndpoint(null, null, null));
    }
}
