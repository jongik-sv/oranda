using System.Text;
using Oranda.Core.Tuning;

namespace Oranda.Core.Tests.Tuning;

// 열 통계 low_value·high_value(RAW) 풀기. 바이트는 Oracle 의 DUMP(…, 16) 결과(실측 형식)를 그대로 옮겼다.
public class ColumnStatValueTests
{
    [Theory]
    [InlineData("80", "0")]
    [InlineData("C102", "1")]
    [InlineData("C10B", "10")]
    [InlineData("C202", "100")]
    [InlineData("C24A46", "7369")]
    [InlineData("C3020102", "10001")]
    [InlineData("C2021834", "123.51")]
    [InlineData("C033", "0.5")]
    [InlineData("BF02", "0.0001")]
    [InlineData("C10201", "1")] // 끝의 0 자리(바이트 01)는 버린다
    [InlineData("C50D222638424E", "1233375565.77")]
    [InlineData("3E6466", "-1")]
    [InlineData("3D5A66", "-1100")]
    [InlineData("3E5E4166", "-7.36")]
    [InlineData("3F3366", "-0.5")]
    [InlineData("FF65", "Infinity")]
    [InlineData("00", "-Infinity")]
    [InlineData("F302", "1E+100")]
    [InlineData("8002", "1E-130")]
    public void Number_decodes_the_oracle_internal_format(string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode("NUMBER", hex));

    [Theory]
    [InlineData("FLOAT")]
    [InlineData("INTEGER")]
    [InlineData("number")]
    public void Number_like_types_use_the_number_format(string type) =>
        Assert.Equal("42", ColumnStatValue.Decode(type, "C12B"));

    [Fact]
    public void Number_with_a_bad_digit_falls_back_to_hex() =>
        Assert.Equal("C1FF", ColumnStatValue.Decode("NUMBER", "C1FF"));

    [Theory]
    [InlineData("787E0C1F010101", "2026-12-31")]
    [InlineData("787E0B140B1F3C", "2026-11-20 10:30:59")]
    [InlineData("77C70101010101", "1999-01-01")]
    [InlineData("64650101010101", "0001-01-01")]
    public void Date_decodes_seven_bytes(string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode("DATE", hex));

    [Theory]
    [InlineData("787E0D010101")] // 6바이트
    [InlineData("787E0D01010101")] // 13월
    [InlineData("3C640101010101")] // 기원전
    public void Bad_dates_fall_back_to_hex(string hex) =>
        Assert.Equal(hex, ColumnStatValue.Decode("DATE", hex));

    [Theory]
    [InlineData("TIMESTAMP(6)", "787E0B140B1F3C075BCD15", "2026-11-20 10:30:59.123456789")]
    [InlineData("TIMESTAMP(3)", "787E0B140B1F3C1DCD6500", "2026-11-20 10:30:59.5")]
    [InlineData("TIMESTAMP(6)", "787E0B14010101", "2026-11-20")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "787E0B140B1F3C000000001C3C", "2026-11-20 10:30:59 UTC")]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", "787E0B140B1F3C00000000", "2026-11-20 10:30:59")]
    public void Timestamp_decodes_seven_eleven_and_thirteen_bytes(string type, string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode(type, hex));

    [Fact]
    public void Timestamp_with_too_many_nanoseconds_falls_back_to_hex() =>
        Assert.Equal("787E0B140B1F3CFFFFFFFF", ColumnStatValue.Decode("TIMESTAMP(9)", "787E0B140B1F3CFFFFFFFF"));

    [Theory]
    [InlineData("BF800000", "1")]
    [InlineData("C0200000", "2.5")]
    [InlineData("407FFFFF", "-1")]
    [InlineData("80000000", "0")]
    public void Binary_float_flips_the_sign_bit_or_all_bits(string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode("BINARY_FLOAT", hex));

    [Theory]
    [InlineData("BFF0000000000000", "1")]
    [InlineData("C00921FB54442D18", "3.141592653589793")]
    [InlineData("3FFFFFFFFFFFFFFF", "-2")]
    public void Binary_double_flips_the_sign_bit_or_all_bits(string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode("BINARY_DOUBLE", hex));

    [Fact]
    public void Binary_float_with_the_wrong_length_falls_back_to_hex() =>
        Assert.Equal("BF80", ColumnStatValue.Decode("BINARY_FLOAT", "BF80"));

    [Fact]
    public void Varchar_decodes_utf8_and_trims_char_padding()
    {
        Assert.Equal("ADAMS", ColumnStatValue.Decode("VARCHAR2", "4144414D53"));
        Assert.Equal("AB", ColumnStatValue.Decode("CHAR", "41422020"));
        Assert.Equal("서울", ColumnStatValue.Decode("VARCHAR2", Hex(Encoding.UTF8.GetBytes("서울"))));
    }

    [Fact]
    public void Varchar_drops_a_character_cut_by_the_statistics_length()
    {
        var bytes = Encoding.UTF8.GetBytes("가나다");
        Assert.Equal("가나", ColumnStatValue.Decode("VARCHAR2", Hex(bytes[..8]))); // 다 의 3바이트 중 2바이트만 남음
    }

    [Fact]
    public void Varchar_that_is_not_utf8_tries_cp949()
    {
        var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)!.GetBytes("한글");
        Assert.Equal("한글", ColumnStatValue.Decode("VARCHAR2", Hex(cp949)));
    }

    [Fact]
    public void Nvarchar_decodes_al16utf16()
    {
        Assert.Equal("서울", ColumnStatValue.Decode("NVARCHAR2", Hex(Encoding.BigEndianUnicode.GetBytes("서울"))));
        Assert.Equal("AB", ColumnStatValue.Decode("NCHAR", "0041004200200020"));
        Assert.Equal("A", ColumnStatValue.Decode("NVARCHAR2", "004100")); // 잘린 반쪽 글자는 버린다
    }

    [Fact]
    public void Text_with_control_characters_falls_back_to_hex() =>
        Assert.Equal("410142", ColumnStatValue.Decode("VARCHAR2", "410142"));

    [Theory]
    [InlineData("RAW")]
    [InlineData("ROWID")]
    [InlineData(null)]
    [InlineData("SDO_GEOMETRY")]
    public void Other_types_stay_hex(string? type) =>
        Assert.Equal("0A0B0C", ColumnStatValue.Decode(type, "0A0B0C"));

    [Fact]
    public void Bytes_and_missing_values()
    {
        Assert.Equal("1", ColumnStatValue.Decode("NUMBER", (object)new byte[] { 0xC1, 0x02 }));
        Assert.Null(ColumnStatValue.Decode("NUMBER", (object?)null));
        Assert.Null(ColumnStatValue.Decode("NUMBER", DBNull.Value));
        Assert.Null(ColumnStatValue.Decode("NUMBER", string.Empty));
        Assert.Null(ColumnStatValue.Decode("NUMBER", Array.Empty<byte>()));
        Assert.Equal("not hex", ColumnStatValue.Decode("NUMBER", "not hex")); // 이미 글이면 그대로
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
