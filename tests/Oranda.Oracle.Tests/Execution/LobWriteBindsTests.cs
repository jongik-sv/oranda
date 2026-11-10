using Oracle.ManagedDataAccess.Client;
using Oranda.Oracle.Execution;

namespace Oranda.Oracle.Tests.Execution;

// LOB 쓰기 바인드(PF5)의 순수 규칙(DB 없이): 바인드 형식, 문장 모양, 행마다 LONG 으로 보낼 수 있는가, 연속 구간 나누기. 실제 값 왕복은 통합 시험 몫.
public sealed class LobWriteBindsTests
{
    private static readonly LobBindColumn[] ClobAndBlob = [new(1, Text: true), new(2, Text: false)];

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("long", false)]
    [InlineData("locator", true)]
    [InlineData(" LOCATOR ", true)]
    public void The_switch_turns_the_old_lob_bind_on_only_for_locator(string? value, bool old)
    {
        Assert.Equal(old ? OracleLobWrite.Locator : OracleLobWrite.LongBind, LobWriteBinds.ModeOf(value));
    }

    [Fact]
    public void Lob_bind_types_map_to_long_and_long_raw_and_other_types_stay()
    {
        Assert.Equal(OracleDbType.Long, LobWriteBinds.LongTypeOf(OracleDbType.Clob));
        Assert.Equal(OracleDbType.Long, LobWriteBinds.LongTypeOf(OracleDbType.NClob));
        Assert.Equal(OracleDbType.LongRaw, LobWriteBinds.LongTypeOf(OracleDbType.Blob));
        Assert.Equal(OracleDbType.Varchar2, LobWriteBinds.LongTypeOf(OracleDbType.Varchar2));
        Assert.Equal(OracleDbType.Raw, LobWriteBinds.LongTypeOf(OracleDbType.Raw));
        Assert.False(LobWriteBinds.IsLob(OracleDbType.Raw));
    }

    [Theory]
    [InlineData("INSERT INTO \"S\".\"T\" (\"ID\", \"CL\", \"BL\") VALUES (:C1, :C2, :C3)", true)]
    [InlineData("insert into t (id, cl, bl) values ( :c1 , :c2 , :c3 )", true)]
    [InlineData("INSERT INTO T (ID, CL, BL) VALUES (:C1, TO_CLOB(:C2), :C3)", false)] // 함수에 감싼 LONG 은 4000바이트로 잘린다
    [InlineData("INSERT INTO T (ID, CL, BL) VALUES (:C1, NVL(:C9, :C2), :C3)", false)] // 함수 괄호 안 쉼표 뒤도 칸이 아니다
    [InlineData("INSERT INTO T (ID, NM, CL, BL) VALUES (:C1, 'a,(b', :C2, :C3)", true)] // 글자 안 쉼표·괄호는 세지 않는다
    [InlineData("INSERT INTO T (ID, CL, BL) SELECT :C1, :C2, :C3 FROM DUAL", false)]
    [InlineData("MERGE INTO T USING (SELECT :C1 AS ID, :C2 AS CL, :C3 AS BL FROM DUAL) S ON (T.ID = S.ID) WHEN NOT MATCHED THEN INSERT (ID, CL, BL) VALUES (S.ID, S.CL, S.BL)", false)]
    [InlineData("INSERT INTO T (ID, CL, BL, CL2) VALUES (:C1, :C2, :C3, :C2)", false)] // 같은 바인드가 두 번
    [InlineData("INSERT INTO T (ID, CL, BL) VALUES (:C1, :C2, :C3) RETURNING ID INTO :R", false)]
    [InlineData("UPDATE T SET CL = :C2, BL = :C3 WHERE ID = :C1", false)]
    public void Long_binds_go_only_into_a_plain_insert_values_list(string sql, bool plain)
    {
        Assert.Equal(plain, LobWriteBinds.IsPlainInsertValues(sql, ["C2", "C3"]));
    }

    [Fact]
    public void A_bind_name_that_is_a_prefix_of_another_is_not_mistaken_for_it()
    {
        const string Sql = "INSERT INTO T (A, B, C, D, E, F, G, H, I, J) VALUES (:C1, :C2, :C3, :C4, :C5, :C6, :C7, :C8, :C9, :C10)";

        Assert.True(LobWriteBinds.IsPlainInsertValues(Sql, ["C1", "C10"]));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4000, true)]
    [InlineData(4001, true)]
    [InlineData(LobWriteBinds.LongBindMaxLength, true)]
    [InlineData(LobWriteBinds.LongBindMaxLength + 1, false)]
    [InlineData(65537, false)]
    [InlineData(0, false)] // 빈 값: LONG 이면 NULL 로 들어가 EMPTY_CLOB·EMPTY_BLOB 이 사라진다
    public void A_row_fits_a_long_bind_only_with_non_empty_values_up_to_the_limit(int length, bool fits)
    {
        object?[] text = [1m, new string('a', length), null];
        object?[] bytes = [1m, null, new byte[length]];

        Assert.Equal(fits, LobWriteBinds.FitsLongBind(text, ClobAndBlob, LongTextCharset.Al32Utf8, out _));
        Assert.Equal(fits, LobWriteBinds.FitsLongBind(bytes, ClobAndBlob, LongTextCharset.Al32Utf8, out _));
    }

    [Fact]
    public void Nulls_fit_and_a_value_of_the_wrong_kind_does_not()
    {
        Assert.True(LobWriteBinds.FitsLongBind([1m, null, DBNull.Value], ClobAndBlob, LongTextCharset.Other, out var width));
        Assert.Equal(0, width);
        Assert.False(LobWriteBinds.FitsLongBind([1m, new byte[] { 1 }, null], ClobAndBlob, LongTextCharset.Al32Utf8, out _)); // CLOB 칸의 byte[]
        Assert.False(LobWriteBinds.FitsLongBind([1m, null, "ab"], ClobAndBlob, LongTextCharset.Al32Utf8, out _)); // BLOB 칸의 string
    }

    [Fact]
    public void Text_goes_as_long_only_when_the_database_character_set_keeps_every_character()
    {
        // 짝 없는 서로게이트는 시험 자료 직렬화에 넣지 않으려고 여기서 만든다.
        const char High = (char)0xD83D;
        const char Low = (char)0xDE00;
        Assert.True(LobWriteBinds.TextFits("plain ascii", LongTextCharset.Other));
        Assert.False(LobWriteBinds.TextFits("한글", LongTextCharset.Other)); // 유니코드가 아닌 DB 문자 집합: ASCII 만
        Assert.True(LobWriteBinds.TextFits("한글", LongTextCharset.Al32Utf8));
        Assert.True(LobWriteBinds.TextFits("emoji " + High + Low + " pair", LongTextCharset.Al32Utf8));
        Assert.False(LobWriteBinds.TextFits("lone " + High + " high", LongTextCharset.Al32Utf8));
        Assert.False(LobWriteBinds.TextFits("lone " + Low + " low", LongTextCharset.Al32Utf8));
        Assert.False(LobWriteBinds.TextFits("end " + High, LongTextCharset.Al32Utf8));
        Assert.False(LobWriteBinds.TextFits("swapped " + Low + High, LongTextCharset.Al32Utf8));

        // UTF8(CESU-8): 보조 평면 글자는 짝이 맞아도 옛 길로(드라이버·서버 변환이 어긋날 수 있다). BMP 한글은 된다.
        Assert.True(LobWriteBinds.TextFits("한글", LongTextCharset.Utf8));
        Assert.False(LobWriteBinds.TextFits("emoji " + High + Low + " pair", LongTextCharset.Utf8));
        Assert.False(LobWriteBinds.TextFits("lone " + High + " high", LongTextCharset.Utf8));
        Assert.False(LobWriteBinds.TextFits("한글", LongTextCharset.Other));
    }

    [Fact]
    public void An_emoji_row_goes_as_long_on_al32utf8_and_the_old_way_on_utf8()
    {
        var emoji = "a" + (char)0xD83D + (char)0xDE00 + "b";
        List<object?[]> rows = [[1m, "x", null], [2m, emoji, null], [3m, "y", null]];

        Assert.Equal([new LobSegment(0, 3, true)], LobWriteBinds.Plan(rows, ClobAndBlob, LongTextCharset.Al32Utf8));
        Assert.Equal(
            [new LobSegment(0, 1, true), new LobSegment(1, 1, false), new LobSegment(2, 1, true)],
            LobWriteBinds.Plan(rows, ClobAndBlob, LongTextCharset.Utf8));
    }

    [Theory]
    [InlineData("AL32UTF8", "Al32Utf8")]
    [InlineData(" al32utf8 ", "Al32Utf8")]
    [InlineData("UTF8", "Utf8")]
    [InlineData("utf8", "Utf8")]
    [InlineData("KO16MSWIN949", "Other")]
    [InlineData("WE8MSWIN1252", "Other")]
    [InlineData(null, "Other")]
    public void Database_character_set_names_map_to_how_much_text_may_go_as_long(string? charset, string expected)
    {
        Assert.Equal(expected, LobWriteBinds.CharsetOf(charset).ToString());
    }

    [Fact]
    public void Rows_split_into_runs_in_order_and_rows_that_cannot_go_as_long_keep_the_lob_bind()
    {
        var big = new string('b', LobWriteBinds.LongBindMaxLength + 1);
        List<object?[]> rows =
        [
            [1m, "a", new byte[] { 1 }],
            [2m, null, null],
            [3m, string.Empty, new byte[] { 1 }], // 빈 CLOB
            [4m, "d", new byte[] { 4 }],
            [5m, big, null],
            [6m, "f", Array.Empty<byte>()],      // 빈 BLOB
            [7m, "g", new byte[] { 7 }],
        ];

        var segments = LobWriteBinds.Plan(rows, ClobAndBlob, LongTextCharset.Al32Utf8);

        Assert.Equal(
            [new LobSegment(0, 2, true), new LobSegment(2, 1, false), new LobSegment(3, 1, true), new LobSegment(4, 2, false), new LobSegment(6, 1, true)],
            segments);
        Assert.Equal(rows.Count, segments.Sum(s => s.Count));
    }

    [Fact]
    public void A_long_run_is_cut_before_rows_times_the_byte_bound_of_its_widest_values_passes_the_budget()
    {
        // 글자는 3바이트 상한: 20000자 = 60000바이트/행.
        var value = new string('w', 20_000);
        var rows = Enumerable.Range(0, 2_000).Select(i => new object?[] { (decimal)i, value, null }).ToList();

        var segments = LobWriteBinds.Plan(rows, ClobAndBlob, LongTextCharset.Al32Utf8);

        // 구간마다 같은 상한(559행)으로 끊고 남은 행이 마지막 구간: 559·559·559·323(원격 #165).
        var perSegment = (int)(LobWriteBinds.LongSegmentBudget / (value.Length * LobWriteBinds.TextBytesPerChar));
        var expected = new List<LobSegment>();
        for (var start = 0; start < rows.Count; start += perSegment)
        {
            expected.Add(new LobSegment(start, Math.Min(perSegment, rows.Count - start), true));
        }

        Assert.True(expected.Count > 2); // 한 번만 끊는 계산이면 잡히지 않는다
        Assert.Equal(expected, segments);
    }

    [Fact]
    public void The_byte_bound_adds_every_lob_column_widest_value_not_only_the_single_longest()
    {
        // CLOB 10000자(30000바이트 상한) + BLOB 10000바이트 = 40000바이트/행. 가장 긴 값 하나(10000)만 보면 3355행까지 한 구간이 된다.
        var text = new string('t', 10_000);
        var bytes = new byte[10_000];
        var rows = Enumerable.Range(0, 1_000).Select(i => new object?[] { (decimal)i, text, bytes }).ToList();

        var segments = LobWriteBinds.Plan(rows, ClobAndBlob, LongTextCharset.Al32Utf8);

        var perSegment = (int)(LobWriteBinds.LongSegmentBudget / 40_000);
        Assert.Equal(perSegment, segments[0].Count);
        Assert.All(segments, s => Assert.True(LobWriteBinds.SegmentBytes(s.Count, [text.Length, bytes.Length], ClobAndBlob) <= LobWriteBinds.LongSegmentBudget));
        Assert.Equal(rows.Count, segments.Sum(s => s.Count));
    }

    [Fact]
    public void The_widest_values_per_column_may_come_from_different_rows()
    {
        // 열마다 가장 긴 값이 다른 행에 있어도 둘을 더한다(드라이버가 원소마다 열의 가장 긴 값만큼 잡는 경우의 상한).
        Assert.Equal(3L * ((100 * LobWriteBinds.TextBytesPerChar) + 50), LobWriteBinds.SegmentBytes(3, [100, 50], ClobAndBlob));
        Assert.Equal(1L, LobWriteBinds.SegmentBytes(1, [0, 0], ClobAndBlob)); // NULL 만 있어도 0 이 아니다
    }

    [Fact]
    public void No_rows_make_no_runs()
    {
        Assert.Empty(LobWriteBinds.Plan([], ClobAndBlob, LongTextCharset.Al32Utf8));
    }

    [Theory]
    [InlineData(24816, true)]
    [InlineData(1461, true)]
    [InlineData(22295, true)]
    [InlineData(1, false)]   // 중복 키: 행 오류라 다시 보내지 않는다
    [InlineData(1400, false)]
    [InlineData(24381, false)] // 배열 DML 오류(겉): 그 자체로는 거부가 아니다
    public void Only_long_bind_error_numbers_count_as_a_refusal(int number, bool refusal)
    {
        Assert.Equal(refusal, LobWriteBinds.IsLongBindRefusal(number));
    }

    [Theory]
    [InlineData(new[] { 24816 }, 1, "ResendInPlace")]
    [InlineData(new[] { 24816 }, 2, "Fail")]
    [InlineData(new[] { 24816 }, 5000, "Fail")]
    [InlineData(new[] { 24381, 1461 }, 1, "ResendInPlace")] // 배치 오류 방식: 겉 24381 안의 행 오류가 거부
    [InlineData(new[] { 24381, 1, 1461 }, 5000, "Fail")]     // 행 오류가 섞여도 여러 행은 제자리에서 다시 보내지 않는다(이미 넣은 행이 있을 수 있다)
    [InlineData(new[] { 24381, 1 }, 1, "NotRefusal")]
    [InlineData(new[] { 1 }, 5000, "NotRefusal")]
    [InlineData(new int[0], 1, "NotRefusal")]
    public void A_refusal_is_resent_in_place_only_for_one_row_and_fails_otherwise(int[] numbers, int rows, string expected)
    {
        Assert.Equal(expected, LobWriteBinds.OnLongBindError(numbers, rows).ToString());
    }

    [Theory]
    [InlineData(1, "ResendInPlace")]
    [InlineData(3, "Fail")]
    public void A_driver_side_bind_rejection_follows_the_same_row_count_rule(int rows, string expected)
    {
        Assert.Equal(expected, LobWriteBinds.OnLongBindError([], rows, driverRejected: true).ToString());
    }
}
