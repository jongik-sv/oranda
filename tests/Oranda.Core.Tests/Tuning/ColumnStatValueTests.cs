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

    // 아래 견본은 시험 DB(Oracle 11.2 XE·26ai, AL32UTF8)에서 ORT_CS_ 시험 테이블에 넣은 값의 low/high_value·endpoint_value 실측이다(두 판 모두 같은 값).
    [Theory]
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", "787C03031507080754D4C01D3C", "2024-03-03 20:06:07.123 UTC")] // 2024-03-04 05:06:07.123 +09:00
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", "6465010101010100000000143C", "0001-01-01 00:00:00 UTC")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "800000003B3C3C80000000", "-0 01:00:00")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "800000034D3C3C80000000", "+3 17:00:00")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "800000003C3C3D9DCD6500", "+0 00:00:01.5")]
    [InlineData("INTERVAL YEAR(3) TO MONTH", "8000000037", "-0-05")]
    [InlineData("INTERVAL YEAR(3) TO MONTH", "8000000145", "+1-09")]
    public void Time_zone_and_interval_values_decode(string type, string hex, string expected) =>
        Assert.Equal(expected, ColumnStatValue.Decode(type, hex));

    [Theory]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "800000003B3C")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "80000000643C3C80000000")] // 시 +40
    [InlineData("INTERVAL YEAR(3) TO MONTH", "8000000050")] // 월 +20
    public void Bad_intervals_fall_back_to_hex(string type, string hex) =>
        Assert.Equal(hex, ColumnStatValue.Decode(type, hex));

    // 문자 열 endpoint_value = 앞 15바이트를 256진 정수로 본 수(유효 15자리). 짧은 코드는 끝의 0 바이트를 버려 글 전체가 나오고, 긴 글은 앞 몇 바이트 + 「…」.
    [Theory]
    [InlineData("VARCHAR2", "218928329261816000000000000000000000", "**")]
    [InlineData("VARCHAR2", "338837934838605000000000000000000000", "AB")]
    [InlineData("VARCHAR2", "420576045541321000000000000000000000", "Q")]
    [InlineData("VARCHAR2", "432765773713116000000000000000000000", "SY")]
    [InlineData("VARCHAR2", "633460216741249000000000000000000000", "z")]
    // 26ai 실측 2바이트 코드: 반올림으로 16진이 4140FF…(AA)·5030FF…(P1)·5A59FF…(ZZ)처럼 한 칸 아래로 내려간 값도 원래 글로 돌아온다.
    [InlineData("VARCHAR2", "338817652429001000000000000000000000", "AA")]
    [InlineData("VARCHAR2", "344030231697140000000000000000000000", "BB")]
    [InlineData("VARCHAR2", "348918292411620000000000000000000000", "C3")]
    [InlineData("VARCHAR2", "354861038425490000000000000000000000", "DX")]
    [InlineData("VARCHAR2", "359667969501555000000000000000000000", "EE")]
    [InlineData("VARCHAR2", "364616877444846000000000000000000000", "F9")]
    [InlineData("VARCHAR2", "370093128037832000000000000000000000", "GG")]
    [InlineData("VARCHAR2", "416377586753365000000000000000000000", "P1")]
    [InlineData("VARCHAR2", "469132134132463000000000000000000000", "ZZ")]
    [InlineData("CHAR", "456922123551065000000000000000000000", "X")]
    [InlineData("VARCHAR2", "375902487384753000000000000000000000", "Hello…")] // 'Hello world long text'
    [InlineData("VARCHAR2", "505648346078211000000000000000000000", "abcdef…")] // 'abcdefg'
    [InlineData("VARCHAR2", "1233608866739590000000000000000000000", "한…")] // '한글값입니다' — 37자리라 앞 5바이트만 확실
    [InlineData("NVARCHAR2", "1825454621499850000000000000000000", "Zz")]
    [InlineData("NVARCHAR2", "1967424061552690000000000000000000", "abc…")] // 7번째 바이트까지만 확실해 뒤에 글자(00xx)가 더 있는지 모른다
    [InlineData("NVARCHAR2", "1107838998252100000000000000000000000", "한글…")] // N'한글N'
    public void Character_endpoint_values_become_their_leading_bytes(string type, string endpoint, string expected) =>
        Assert.Equal(expected, ColumnStatValue.HistogramEndpoint(type, null, null, endpoint));

    [Theory]
    [InlineData("DATE", "2460311.5", "2024-01-01 12:00:00")]
    [InlineData("DATE", "2460316", "2024-01-06")]
    [InlineData("DATE", "2458852.58333333", "2020-01-03 14:00:00")] // 26ai HYBRID 실측
    [InlineData("TIMESTAMP(6)", "2460374.21258245", "2024-03-04 05:06:07")]
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", "2460373.83758244", "2024-03-03 20:06:07 UTC")]
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", "1721424", "0001-01-01 00:00:00 UTC")] // 1582-10-15 앞은 율리우스력(Oracle 'J')
    [InlineData("TIMESTAMP(3) WITH TIME ZONE", "1721424.75283565", "0001-01-01 18:04:05 UTC")]
    [InlineData("DATE", "2299161", "1582-10-15")]
    [InlineData("DATE", "2299160", "1582-10-04")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "-3600", "-0 01:00:00")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "104400", "+1 05:00:00")]
    [InlineData("INTERVAL DAY(3) TO SECOND(6)", "320400", "+3 17:00:00")]
    [InlineData("INTERVAL YEAR(3) TO MONTH", "-5", "-0-05")]
    [InlineData("INTERVAL YEAR(3) TO MONTH", "21", "+1-09")]
    [InlineData("NUMBER", "1.5", "1.5")]
    [InlineData(null, "338837934838605000000000000000000000", "338837934838605000000000000000000000")] // 자료형을 모르면 그대로
    [InlineData("RAW", "12345", "12345")]
    public void Endpoint_values_follow_the_data_type(string? type, string endpoint, string expected) =>
        Assert.Equal(expected, ColumnStatValue.HistogramEndpoint(type, null, null, endpoint));

    [Fact]
    public void Histogram_endpoint_prefers_the_raw_actual_value_then_the_text()
    {
        // 26ai: NVARCHAR2 의 endpoint_actual_value 는 AL16UTF16 바이트를 글로 보여 깨진다 — raw 를 풀어 쓴다.
        Assert.Equal("한글N", ColumnStatValue.HistogramEndpoint("NVARCHAR2", "D55CAE00004E", "?\\?  N", "1107838998252100000000000000000000000"));
        Assert.Equal("Hello world long text", ColumnStatValue.HistogramEndpoint("VARCHAR2", "48656C6C6F20776F726C64206C6F6E672074657874", "Hello world long text", "375902487384753000000000000000000000"));
        Assert.Equal("ABCDEFGH", ColumnStatValue.HistogramEndpoint("VARCHAR2", null, "ABCDEFGH", "338837934838605000000000000000000000")); // 11g: 글만 있다
        Assert.Equal("ABCDEFGH", ColumnStatValue.HistogramEndpoint(null, "4142434445464748", "ABCDEFGH", "1")); // 자료형을 모르면 raw 대신 글
        Assert.Null(ColumnStatValue.HistogramEndpoint("VARCHAR2", null, null, null));
        Assert.Equal("0", ColumnStatValue.HistogramEndpoint("VARCHAR2", null, null, "0")); // 풀 수 없는 값은 그대로
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}
