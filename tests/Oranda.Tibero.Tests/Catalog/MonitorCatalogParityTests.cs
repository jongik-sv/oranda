using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle;
using Oranda.Oracle.Catalog;
using Oranda.Tibero.Catalog;

namespace Oranda.Tibero.Tests.Catalog;

/// <summary>
/// 실행 계획·세션·잠금 카탈로그(X4)의 Tibero 판이 같은 id 의 Oracle 판과 같은 바인드를 쓰는지, Tibero 판이 없는 id 가 문서의 「없음」 목록과 같은지
/// (docs/design/tibero-monitor-catalog.md). 서버 없이 돈다. 열 순서·이름은 서버 실측(읽기)으로 확인했다.
/// </summary>
public partial class MonitorCatalogParityTests
{
    private static readonly string[] Folders = ["plan", "session", "lock", "tuning", "sql", "app"];

    /// <summary>모든 id 를 다 본 폴더(없음 목록을 단정한다).</summary>
    private static readonly string[] CompleteFolders = Folders;

    /// <summary>Tibero 에 해당 뷰·기능이 없어 파일을 두지 않은 id(문서 2장).</summary>
    private static readonly string[] Missing =
    [
        "plan.monitor_report_html", "session.trace_file", "session.wait_history", "lock.deadlocks", "lock.locked_row",
        "tuning.plan_controls", "tuning.recommend_literals", "tuning.shared_cursor", "tuning.trace_file", "tuning.trace_file_self",
        "tuning.trace_lines", "app.disconnect_session", "app.trace_event_off", "app.trace_event_on", "app.trace_parse",
    ];

    /// <summary>
    /// Tibero 에만 있는 id(문서 2장 끝). Oracle 은 같은 값을 다른 길로 구해 파일이 없다 — 억지로 Oracle 판을 만들지 않는다.
    /// <c>plan.explain_hash</c>: Oracle 은 예상 계획 해시를 DBMS_XPLAN 글의 「Plan hash value」에서 읽고, Tibero 글에는 그 줄이 없어
    /// PLAN_TABLE.SQL_ID·CHILD_NUMBER → V$SQL 로 찾는다(P1 M4 실측).
    /// </summary>
    private static readonly string[] TiberoOnly = ["plan.explain_hash"];

    private static List<CatalogQuery> Queries(IEnumerable<CatalogSource> sources) =>
        [.. sources
            .Where(s => Folders.Contains(CatalogParser.TopFolder(s.Path)))
            .Select(s => CatalogParser.Parse(s).Query)
            .OfType<CatalogQuery>()];

    private static readonly List<CatalogQuery> Oracle = Queries(EmbeddedCatalog.ReadSources(typeof(OracleAssembly).Assembly));
    private static readonly List<CatalogQuery> Tibero = Queries(TiberoEmbeddedCatalog.Sources);

    public static TheoryData<string> TiberoIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in Tibero.Select(q => q.Header.Id).Distinct().Order(StringComparer.Ordinal))
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TiberoIds))]
    public void Tibero_file_uses_the_same_binds_as_the_oracle_file(string id)
    {
        if (TiberoOnly.Contains(id))
        {
            return; // 짝이 없는 id(아래 Tibero_only_ids_have_no_oracle_file 가 따로 단정)
        }

        var oracle = Oracle.Where(q => q.Header.Id == id).MaxBy(q => q.Header.MinVersion);
        Assert.NotNull(oracle); // Oracle 에 없는 id 를 Tibero 에만 두지 않는다

        foreach (var tibero in Tibero.Where(q => q.Header.Id == id))
        {
            Assert.Equal(Binds(oracle.Sql), Binds(tibero.Sql));
            Assert.Equal(Placeholders(oracle.Sql), Placeholders(tibero.Sql)); // @PFX@·@TABLE@ 등 호출자가 채우는 자리
        }
    }

    private static string[] Placeholders(string sql) =>
        [.. PlaceholderRegex().Matches(sql).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex("@[A-Z_]+@")]
    private static partial Regex PlaceholderRegex();

    [Fact]
    public void Tibero_only_ids_have_no_oracle_file()
    {
        // 목록의 id 는 Tibero 파일이 있고 Oracle 파일은 없다. Oracle 판이 생기면 목록에서 빼고 바인드 대조를 받게 한다.
        foreach (var id in TiberoOnly)
        {
            Assert.Contains(Tibero, q => q.Header.Id == id);
            Assert.DoesNotContain(Oracle, q => q.Header.Id == id);
        }
    }

    [Fact]
    public void Oracle_ids_without_a_tibero_file_are_the_documented_missing_ones()
    {
        var tiberoIds = Tibero.Select(q => q.Header.Id).ToHashSet(StringComparer.Ordinal);
        var withoutTibero = Oracle.Select(q => q.Header.Id)
            .Where(id => CompleteFolders.Contains(id[..id.IndexOf('.', StringComparison.Ordinal)]))
            .Distinct().Where(id => !tiberoIds.Contains(id)).Order(StringComparer.Ordinal);

        Assert.Equal(Missing.Order(StringComparer.Ordinal), withoutTibero);
    }

    [Fact]
    public void Bind_scanner_ignores_strings_comments_and_assignments()
    {
        Assert.Equal(["a", "b"], Binds("SELECT ':x' || :a FROM dual -- :y\n WHERE b = :b AND :A IS NULL AND x := 1"));
    }

    private static string[] Binds(string sql)
    {
        var text = CommentRegex().Replace(StringRegex().Replace(sql, "''"), string.Empty);
        return [.. BindRegex().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("'[^']*'")]
    private static partial Regex StringRegex();

    [GeneratedRegex("--[^\n]*")]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"(?<![:\w]):([A-Za-z_]\w*)")]
    private static partial Regex BindRegex();
}
