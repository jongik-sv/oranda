using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>
/// 스키마 브라우저 표식 보조 질의(Tibero): 카탈로그 파일 머리·바인드·requires, 모르는 열은 NULL(= 표식 없음), 목록과 따로 소유자마다 읽어 캐시에 넣는 흐름.
/// </summary>
public partial class TiberoObjectMarksTests
{
    private static readonly Core.Catalog.SqlCatalog Catalog = TiberoEmbeddedCatalog.Load();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mark_queries_resolve_bind_only_the_owner_and_follow_the_dictionary_prefix(bool dictionary)
    {
        await using var session = Session(dictionary);

        foreach (var id in TiberoMetadataProvider.MarkIds)
        {
            var sql = TiberoCatalogQuery.Sql(session, id);
            Assert.False(string.IsNullOrWhiteSpace(sql), id);
            Assert.Contains(dictionary ? "dba_" : "all_", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("@PFX@", sql, StringComparison.Ordinal);

            var query = Catalog.Get(id, 6, Dbms.Tibero);
            Assert.Equal(6, query.Header.MinVersion);
            Assert.Equal(Core.Catalog.CatalogLicense.None, query.Header.License);
            Assert.Equal(["owner"], BindName().Matches(Body(query.Sql)).Select(m => m.Groups[1].Value).Distinct());
            Assert.Contains("owner = :owner", query.Sql, StringComparison.Ordinal);
            Assert.NotEmpty(query.Header.Requires);
        }

        Assert.Contains(ObjectMarkRows.Jobs, TiberoMetadataProvider.MarkIds); // 작업은 meta.jobs 로 목록에 넣는다(2026-11-21)
    }

    [Fact]
    public void Unknown_tibero_columns_are_null()
    {
        var tables = Catalog.Get(ObjectMarkRows.Tables, 6, Dbms.Tibero).Sql;
        Assert.Contains("NULL AS cluster_name", tables, StringComparison.Ordinal);
        Assert.Contains("'NO' AS is_object", tables, StringComparison.Ordinal);
        Assert.DoesNotContain("object_tables", tables, StringComparison.Ordinal); // Tibero 에 없는 뷰는 읽지 않는다(머리 주석에 적었다)

        Assert.Contains("NULL AS base_object_type", Catalog.Get(ObjectMarkRows.Triggers, 6, Dbms.Tibero).Sql, StringComparison.Ordinal);
        var synonyms = Catalog.Get(ObjectMarkRows.Synonyms, 6, Dbms.Tibero).Sql;
        Assert.Contains("THEN SUBSTR(s.org_object_name, INSTR(s.org_object_name, '@', -1) + 1) END AS db_link", synonyms, StringComparison.Ordinal); // "이름"@링크(실측)
        Assert.Contains("CASE WHEN x.db_link IS NULL THEN", synonyms, StringComparison.Ordinal); // 원격이면 대상을 찾지 않는다
        Assert.Contains("s.owner <> 'PUBLIC'", synonyms, StringComparison.Ordinal); // PUBLIC 동의어 수천 개는 읽지 않는다
        Assert.Contains("org_object_owner", synonyms, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marks_are_read_after_the_list_per_owner_and_land_in_the_cache()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var bus = new RecordingEventBus();
        var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), bus, Timeout.InfiniteTimeSpan, Recording(calls, ById(
            ("meta.objects", Table(
                ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"],
                ["SCOTT", "GTT", "TABLE", "VALID", null, null],
                ["SCOTT", "TRG", "TRIGGER", "VALID", null, null],
                ["SCOTT", "S", "SYNONYM", "VALID", null, null])),
            (ObjectMarkRows.Tables, Table(["TABLE_NAME"], ["GTT", "YES", "SESSION", null, null, "RANGE|NONE|", "NO", "NO"])),
            (ObjectMarkRows.Triggers, Table(["TRIGGER_NAME"], ["TRG", "BEFORE DDL", null, "DROP OR TRUNCATE", "ENABLED"])),
            (ObjectMarkRows.Synonyms, Table(["SYNONYM_NAME"], ["S", null, null])))));
        await using var session = Session();

        await provider.RefreshAsync(session, CancellationToken.None);
        Assert.Equal(["meta.objects", "meta.jobs"], calls.Select(c => c.Id)); // 목록 질의는 그대로(표식을 같이 읽지 않는다), 작업은 따로 덧붙인다

        Assert.Empty(await provider.ListObjectMarksAsync(session, "SCOTT", CancellationToken.None));
        for (var i = 0; i < 300 && !bus.Published.OfType<ObjectMarksRefreshed>().Any(); i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(TiberoMetadataProvider.MarkIds, calls.Skip(2).Select(c => c.Id));
        Assert.All(calls.Skip(2), c => Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT" }, c.Binds));
        var marks = await provider.ListObjectMarksAsync(session, "SCOTT", CancellationToken.None);
        Assert.Equal(new ObjectMark("GTT_SESSION PART:RANGE", null), marks[new DbObjectRef("SCOTT", "GTT", "TABLE")]);
        Assert.Equal(new ObjectMark("BEFORE DDL", null), marks[new DbObjectRef("SCOTT", "TRG", "TRIGGER")]);
        Assert.Equal(new ObjectMark(null, "BROKEN"), marks[new DbObjectRef("SCOTT", "S", "SYNONYM")]);
    }

    [Fact]
    public void Index_status_and_partitioned_come_from_the_trailing_columns()
    {
        object?[][] idx =
        [
            ["I_U", "NONUNIQUE", "NORMAL", "A", 1m, "ASC", null, "TS", null, "UNUSABLE", "NO"],
            ["I_P", "UNIQUE", "NORMAL", "B", 1m, "ASC", null, "TS", null, "VALID", "YES"],
            ["I_OLD", "UNIQUE", "NORMAL", "C", 1m, "ASC", null, "TS", null],
        ];
        var info = new TableInfo(new DbObjectRef("S", "T", "TABLE"), []) { Indexes = MetadataRows.BuildIndexes(idx) };

        var indexes = TiberoMetadataProvider.WithTableEditMetadata(info, [], [], idx, []).Indexes.ToDictionary(i => i.Name);

        Assert.Equal((true, false), (indexes["I_U"].IsUnusable, indexes["I_U"].IsPartitioned));
        Assert.Equal((false, true), (indexes["I_P"].IsUnusable, indexes["I_P"].IsPartitioned));
        Assert.Equal((false, false), (indexes["I_OLD"].IsUnusable, indexes["I_OLD"].IsPartitioned)); // 예전 행 모양 = 모름
        Assert.Contains("i.status, i.partitioned", Catalog.Get("meta.indexes", 6, Dbms.Tibero).Sql, StringComparison.Ordinal);
    }

    private static string Body(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();
}
