using Oranda.Core.Metadata;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

public class TiberoSchemaAdminProviderTests
{
    private static readonly string[] ObjectColumns = ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "CREATED", "LAST_DDL_TIME", "SIZE_BYTES"];
    private static readonly string[] SourceColumns = ["OWNER", "NAME", "TYPE", "LINE", "TEXT"];
    private static readonly string[] ColumnColumns =
        ["OWNER", "TABLE_NAME", "OBJECT_TYPE", "COLUMN_NAME", "DATA_TYPE", "DATA_LENGTH", "DATA_PRECISION", "DATA_SCALE", "NULLABLE"];

    private static List<(string Id, IReadOnlyDictionary<string, object?>? Binds)> NewCalls() => [];

    /// <summary>정규식 검사에서만 <paramref name="error"/> 를 던지고 나머지는 <paramref name="answer"/> 로 답하는 실행기.</summary>
    private static CatalogRunner ThrowingOnRegexCheck(Exception error, Func<string, TiberoSessionQuery.Table> answer, List<string> ids) =>
        (_, id, _, _) =>
        {
            ids.Add(id);
            return id == TiberoSchemaAdminProvider.RegexCheckId ? Task.FromException<TiberoSessionQuery.Table>(error) : Task.FromResult(answer(id));
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_catalog_id_the_provider_uses_resolves_with_its_placeholders_replaced(bool dictionary)
    {
        await using var session = Session(dictionary);

        foreach (var id in TiberoSchemaAdminProvider.CatalogIds)
        {
            var sql = TiberoCatalogQuery.Sql(session, id, TiberoSchemaAdminProvider.Replacements(session, id));

            Assert.False(string.IsNullOrWhiteSpace(sql), id);
            Assert.DoesNotContain(TiberoCatalogQuery.PrefixToken, sql, StringComparison.Ordinal);
            Assert.DoesNotContain(TiberoSchemaAdminProvider.SegmentsToken, sql, StringComparison.Ordinal);
        }

        Assert.Equal(6, TiberoSchemaAdminProvider.CatalogIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(false, "FROM (SELECT USER AS owner, segment_name, segment_type, bytes FROM user_segments) g")]
    [InlineData(true, "FROM dba_segments g")]
    public async Task Object_search_takes_its_sizes_from_dba_segments_or_the_users_own_segments(bool dictionary, string expected)
    {
        await using var session = Session(dictionary);

        var sql = TiberoCatalogQuery.Sql(session, TiberoSchemaAdminProvider.ObjectSearchId, TiberoSchemaAdminProvider.Replacements(session, TiberoSchemaAdminProvider.ObjectSearchId));

        Assert.Contains(expected, sql, StringComparison.Ordinal);
        Assert.Contains(dictionary ? "FROM dba_objects o" : "FROM all_objects o", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Null(TiberoSchemaAdminProvider.Replacements(session, TiberoSchemaAdminProvider.SourceSearchId));
    }

    [Fact]
    public async Task Objects_map_the_row_and_bind_every_condition()
    {
        var calls = NewCalls();
        var created = new DateTime(2024, 1, 2, 3, 4, 5);
        var changed = new DateTime(2024, 2, 3, 4, 5, 6);
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(ObjectColumns,
            ["HR", "EMP", "TABLE", "VALID", created, changed, 8192L],
            ["HR", "V_EMP", "VIEW", "INVALID", null, null, null])));
        await using var session = Session();

        var rows = await provider.SearchObjectsAsync(session, new ObjectSearchQuery
        {
            Owner = "HR",
            ObjectTypes = ["TABLE", "VIEW"],
            NamePattern = "E%",
            InvalidOnly = true,
            CreatedAfter = created,
            ModifiedAfter = changed,
            MinSizeBytes = 10,
            MaxSizeBytes = 20,
            Limit = 50,
        }, CancellationToken.None);

        Assert.Equal(
        [
            new ObjectSearchRow(new DbObjectRef("HR", "EMP", "TABLE"), "VALID", created, changed, 8192),
            new ObjectSearchRow(new DbObjectRef("HR", "V_EMP", "VIEW"), "INVALID", null, null, null),
        ], rows);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal(TiberoSchemaAdminProvider.ObjectSearchId, id);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["owner"] = "HR",
            ["types"] = "|TABLE|VIEW|",
            ["name_pattern"] = "E%",
            ["invalid_only"] = 1,
            ["created_after"] = created,
            ["modified_after"] = changed,
            ["min_size"] = 10L,
            ["max_size"] = 20L,
            ["max_rows"] = 50,
        }, binds);
    }

    [Fact]
    public async Task Objects_default_to_all_types_no_limit_and_null_conditions()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(ObjectColumns)));
        await using var session = Session();

        var rows = await provider.SearchObjectsAsync(session, new ObjectSearchQuery { Limit = 0 }, CancellationToken.None);

        Assert.Empty(rows);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["owner"] = null,
            ["types"] = null,
            ["name_pattern"] = null,
            ["invalid_only"] = 0,
            ["created_after"] = null,
            ["modified_after"] = null,
            ["min_size"] = null,
            ["max_size"] = null,
            ["max_rows"] = int.MaxValue,
        }, Assert.Single(calls).Binds);
    }

    [Fact]
    public async Task Source_search_sends_escaped_text_as_binds_and_trims_line_ends()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(SourceColumns,
            ["HR", "PKG_A", "PACKAGE BODY", 12L, "  x := a.b;\r\n"],
            ["HR", "PKG_A", "PACKAGE BODY", 30, "a.b"])));
        await using var session = Session();

        var result = await provider.SearchSourceAsync(session,
            new SourceSearchQuery { Owner = "HR", ObjectTypes = ["PACKAGE BODY"], Text = "a.b", Limit = 5 }, CancellationToken.None);

        Assert.False(result.Capped);
        Assert.Equal(
        [
            new SourceSearchHit(new DbObjectRef("HR", "PKG_A", "PACKAGE BODY"), 12, "  x := a.b;"),
            new SourceSearchHit(new DbObjectRef("HR", "PKG_A", "PACKAGE BODY"), 30, "a.b"),
        ], result.Rows);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal(TiberoSchemaAdminProvider.SourceSearchId, id);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["owner"] = "HR",
            ["types"] = "|PACKAGE BODY|",
            ["needle"] = "a.b",
            ["pattern"] = "a\\.b",
            ["flags"] = "i",
            ["max_rows"] = 6,
        }, binds);
    }

    [Fact]
    public async Task Source_search_is_capped_when_the_server_returned_more_than_the_limit()
    {
        var provider = new TiberoSchemaAdminProvider(Recording(NewCalls(), _ => Table(SourceColumns,
            ["HR", "P", "PROCEDURE", 1, "a"], ["HR", "P", "PROCEDURE", 2, "a"], ["HR", "P", "PROCEDURE", 3, "a"])));
        await using var session = Session();

        var result = await provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "a", Limit = 2 }, CancellationToken.None);

        Assert.True(result.Capped);
        Assert.Equal([1, 2], result.Rows.Select(r => r.Line));
    }

    [Fact]
    public async Task Source_search_without_a_limit_asks_for_one_thousand_and_one_rows()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(SourceColumns)));
        await using var session = Session();

        await provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "a", Limit = 0, MatchCase = true }, CancellationToken.None);

        var binds = Assert.Single(calls).Binds!;
        Assert.Equal(1001, binds["max_rows"]);
        Assert.Equal("c", binds["flags"]);
    }

    [Fact]
    public async Task Regex_source_search_checks_the_pattern_first_and_sends_no_needle()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(SourceColumns)));
        await using var session = Session();

        await provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "ab+", UseRegex = true, MatchCase = true }, CancellationToken.None);

        Assert.Equal([TiberoSchemaAdminProvider.RegexCheckId, TiberoSchemaAdminProvider.SourceSearchId], calls.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, object?> { ["pattern"] = "ab+", ["flags"] = "c" }, calls[0].Binds);
        Assert.Null(calls[1].Binds!["needle"]);
        Assert.Equal("ab+", calls[1].Binds!["pattern"]);
    }

    [Fact]
    public async Task Whole_word_search_wraps_the_escaped_text_in_boundary_conditions()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(SourceColumns)));
        await using var session = Session();

        await provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "emp", WholeWord = true }, CancellationToken.None);

        Assert.Equal("(^|[^[:alnum:]_$#])(emp)($|[^[:alnum:]_$#])", Assert.Single(calls).Binds!["pattern"]);
    }

    [Fact]
    public async Task An_empty_search_text_is_a_pattern_error_before_any_query()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(SourceColumns)));
        await using var session = Session();

        await Assert.ThrowsAsync<SearchPatternException>(() => provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "" }, CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_server_error_from_the_regex_check_is_a_pattern_error_and_stops_the_search()
    {
        var ids = new List<string>();
        var error = ServerError(-12345, "TBR-12345: invalid regular expression");
        var provider = new TiberoSchemaAdminProvider(ThrowingOnRegexCheck(error, _ => Table(SourceColumns), ids));
        await using var session = Session();

        var thrown = await Assert.ThrowsAsync<SearchPatternException>(() =>
            provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "(", UseRegex = true }, CancellationToken.None));

        Assert.Same(error, thrown.InnerException);
        Assert.StartsWith("정규식이 올바르지 않다", thrown.Message, StringComparison.Ordinal);
        Assert.Equal([TiberoSchemaAdminProvider.RegexCheckId], ids);
    }

    [Theory]
    [InlineData(-12001)] // 세션 끊김
    [InlineData(-12040)] // 취소
    [InlineData(-8033)] // 객체 없음(권한 없음 포함)
    [InlineData(-8053)] // 권한 부족
    [InlineData(-90499)] // 분류표에 없는 드라이버 쪽 번호(통신·내부) — 패턴 탓이 아니다
    [InlineData(-590799)] // 분류표에 없는 드라이버 내부 번호
    public async Task Classified_server_errors_from_the_regex_check_are_thrown_as_they_are(int number)
    {
        var error = ServerError(number, "TBR-" + Math.Abs(number) + ": server error");
        var provider = new TiberoSchemaAdminProvider(ThrowingOnRegexCheck(error, _ => Table(SourceColumns), []));
        await using var session = Session();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "a", UseRegex = true }, CancellationToken.None));

        Assert.Same(error, thrown);
    }

    [Fact]
    public async Task A_plain_search_does_not_run_the_regex_check_and_does_not_map_its_own_errors()
    {
        var error = ServerError(-12345, "TBR-12345: whatever");
        var provider = new TiberoSchemaAdminProvider(Throwing(error));
        await using var session = Session();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => provider.SearchSourceAsync(session, new SourceSearchQuery { Text = "a" }, CancellationToken.None));

        Assert.Same(error, thrown);
    }

    [Fact]
    public async Task Columns_map_the_row_with_type_text_and_bind_the_pattern()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(ColumnColumns,
            ["HR", "EMP", "TABLE", "ENAME", "VARCHAR", 30L, null, null, "N"],
            ["HR", "EMP", "TABLE", "SAL", "NUMBER", null, 10, 2, "Y"],
            ["HR", "V_EMP", null, "DEPTNO", "NUMBER", null, null, null, null])));
        await using var session = Session();

        var result = await provider.SearchColumnsAsync(session, new ColumnSearchQuery { Owner = "HR", NamePattern = "%E%", Limit = 10 }, CancellationToken.None);

        Assert.False(result.Capped);
        Assert.Equal(
        [
            new ColumnSearchHit(new DbObjectRef("HR", "EMP", "TABLE"), "ENAME", "VARCHAR(30)", false),
            new ColumnSearchHit(new DbObjectRef("HR", "EMP", "TABLE"), "SAL", "NUMBER(10,2)", true),
            new ColumnSearchHit(new DbObjectRef("HR", "V_EMP", "TABLE"), "DEPTNO", "NUMBER", true),
        ], result.Rows);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal(TiberoSchemaAdminProvider.ColumnSearchId, id);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name_pattern"] = "%E%", ["max_rows"] = 11 }, binds);
    }

    [Fact]
    public async Task Columns_default_to_every_name_one_thousand_rows_and_report_the_cap()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(ColumnColumns,
            ["HR", "T", "TABLE", "A", "DATE", null, null, null, "Y"], ["HR", "T", "TABLE", "B", "DATE", null, null, null, "Y"])));
        await using var session = Session();

        var capped = await provider.SearchColumnsAsync(session, new ColumnSearchQuery { Limit = 1 }, CancellationToken.None);
        await provider.SearchColumnsAsync(session, new ColumnSearchQuery { Limit = -1 }, CancellationToken.None);

        Assert.True(capped.Capped);
        Assert.Equal(["A"], capped.Rows.Select(r => r.ColumnName));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = null, ["name_pattern"] = "%", ["max_rows"] = 2 }, calls[0].Binds);
        Assert.Equal(1001, calls[1].Binds!["max_rows"]);
    }

    [Theory]
    [InlineData("VARCHAR", 30L, null, null, "VARCHAR(30)")]
    [InlineData("VARCHAR2", 30L, null, null, "VARCHAR2(30)")]
    [InlineData("NVARCHAR", 10L, null, null, "NVARCHAR(10)")]
    [InlineData("CHAR", 1L, null, null, "CHAR(1)")]
    [InlineData("RAW", 16L, null, null, "RAW(16)")]
    [InlineData("VARCHAR", null, null, null, "VARCHAR")]
    [InlineData("NUMBER", null, 10L, 2L, "NUMBER(10,2)")]
    [InlineData("NUMBER", null, 10L, 0L, "NUMBER(10)")]
    [InlineData("NUMBER", null, 10L, null, "NUMBER(10)")]
    [InlineData("NUMBER", null, null, 0L, "NUMBER(*,0)")]
    [InlineData("NUMBER", null, null, null, "NUMBER")]
    [InlineData("FLOAT", null, 126L, null, "FLOAT(126)")]
    [InlineData("TIMESTAMP(6)", null, null, null, "TIMESTAMP(6)")]
    [InlineData("DATE", 7L, null, null, "DATE")]
    public void Type_text_adds_length_or_precision(string dataType, long? length, long? precision, long? scale, string expected) =>
        Assert.Equal(expected, TiberoSchemaAdminProvider.TypeText(dataType, length, precision, scale));

    [Fact]
    public async Task References_map_direction_and_bind_the_object()
    {
        var calls = NewCalls();
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(["DIRECTION", "OWNER_NAME", "OBJECT_NAME", "OBJECT_TYPE"],
            ["USED_BY", "HR", "V_EMP", "VIEW"],
            ["USES", "HR", "EMP", "TABLE"])));
        await using var session = Session();

        var refs = await provider.GetReferencesAsync(session, new DbObjectRef("HR", "PKG_A", "PACKAGE BODY"), CancellationToken.None);

        Assert.Equal(
        [
            new ObjectReference(true, new DbObjectRef("HR", "V_EMP", "VIEW")),
            new ObjectReference(false, new DbObjectRef("HR", "EMP", "TABLE")),
        ], refs);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal(TiberoSchemaAdminProvider.ReferencesId, id);
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "HR", ["name"] = "PKG_A", ["type"] = "PACKAGE BODY" }, binds);
    }

    [Fact]
    public async Task Recycle_bin_maps_items_and_reads_missing_undrop_and_purge_flags_as_no()
    {
        var calls = NewCalls();
        var dropped = new DateTime(2024, 5, 6, 7, 8, 9);
        var provider = new TiberoSchemaAdminProvider(Recording(calls, _ => Table(
            ["OBJECT_NAME", "ORIGINAL_NAME", "TYPE", "TS_NAME", "DROPTIME", "CAN_UNDROP", "CAN_PURGE", "SPACE"],
            ["BIN$A", "EMP", "TABLE", "USR", "2024-05-06:07:08:09", null, null, 8L],
            ["BIN$B", null, null, null, dropped, "YES", "YES", null],
            ["BIN$C", "X", "INDEX", null, "not a date", "NO", null, 1])));
        await using var session = Session();

        var items = await provider.ListRecycleBinAsync(session, CancellationToken.None);

        Assert.Equal(
        [
            new RecycleBinItem("BIN$A", "EMP", "TABLE", "USR", dropped, false, false, 8),
            new RecycleBinItem("BIN$B", string.Empty, string.Empty, null, dropped, true, true, null),
            new RecycleBinItem("BIN$C", "X", "INDEX", null, null, false, false, 1),
        ], items);
        var (id, binds) = Assert.Single(calls);
        Assert.Equal(TiberoSchemaAdminProvider.RecycleBinId, id);
        Assert.Null(binds);
    }

    [Theory]
    [InlineData("2024-05-06:07:08:09")]
    [InlineData("2024-05-06 07:08:09")]
    [InlineData("2024/05/06 07:08:09")]
    [InlineData("2024-05-06T07:08:09")]
    [InlineData("  2024-05-06 07:08:09 ")]
    public void Drop_time_reads_the_known_string_shapes(string text) =>
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), TiberoSchemaAdminProvider.DropTime(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("garbage")]
    public void Drop_time_is_null_when_it_cannot_be_read(string? text) => Assert.Null(TiberoSchemaAdminProvider.DropTime(text));

    [Fact]
    public async Task Server_errors_of_every_other_query_are_thrown_as_they_are()
    {
        var error = ServerError(-8033, "TBR-8033: Specified schema object was not found.");
        var provider = new TiberoSchemaAdminProvider(Throwing(error));
        await using var session = Session();
        var obj = new DbObjectRef("HR", "EMP", "TABLE");

        var thrown = new[]
        {
            await Assert.ThrowsAnyAsync<Exception>(() => provider.SearchObjectsAsync(session, new ObjectSearchQuery(), CancellationToken.None)),
            await Assert.ThrowsAnyAsync<Exception>(() => provider.SearchColumnsAsync(session, new ColumnSearchQuery(), CancellationToken.None)),
            await Assert.ThrowsAnyAsync<Exception>(() => provider.GetReferencesAsync(session, obj, CancellationToken.None)),
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ListRecycleBinAsync(session, CancellationToken.None)),
        };

        Assert.All(thrown, t => Assert.Same(error, t));
    }
}
