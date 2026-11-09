using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Tests.Transfer.Copy;

/// <summary>Tibero 사전 점검 방언(TB3a)과 <c>copy.*</c> 카탈로그(서버 없음). 서버로 도는 확인은 <c>TiberoCopyCatalogIt</c>.</summary>
[Trait("Category", "Catalog")]
public sealed partial class TiberoCopyPrecheckDialectTests
{
    private static readonly SqlCatalog Catalog = TiberoEmbeddedCatalog.Load();

    private static readonly TiberoCopyPrecheckDialect Dialect = TiberoCopyPrecheckDialect.Instance;

    [Fact]
    public async Task Tibero_dialect_has_no_read_privilege_and_no_string_size_blockers()
    {
        Assert.False(Dialect.SupportsReadPrivilege);

        // Oracle 판이라면 STANDARD 한도를 넘어 MAX_STRING_SIZE 를 묻는 열들. Tibero 판은 묻지 않고 빈 목록이다.
        var table = new TableInfo(new DbObjectRef("SCOTT", "T", "TABLE"),
            [new ColumnInfo("C", 1, "VARCHAR2") { Length = 5000 }, new ColumnInfo("N", 2, "NVARCHAR2") { Length = 3000 }, new ColumnInfo("R", 3, "RAW") { Length = 3000 }]);
        var blockers = await Dialect.ExtendedStringBlockersAsync(table,
            (_, _) => throw new InvalidOperationException("Tibero 판은 대상 파라미터를 묻지 않는다"), CancellationToken.None);

        Assert.Empty(blockers);
    }

    [Fact]
    public void Recreate_is_blocked_with_a_reason_until_tb4()
    {
        var reason = Dialect.RecreateBlockedReason(null!);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(TiberoCopyPrecheckDialect.RecreateBlocked, reason);
    }

    [Fact]
    public void Not_restored_probes_are_the_oracle_list_without_fga_and_all_resolve_in_the_tibero_catalog()
    {
        var ids = Dialect.NotRestoredProbes.Select(p => p.CatalogId).ToList();

        Assert.Equal(["copy.col_priv_count", "copy.other_grantor_count", "copy.mview_log_count", "copy.vpd_policy_count"], ids);
        Assert.DoesNotContain("copy.fga_policy_count", ids);
        Assert.All(ids, id => Assert.Equal(Dbms.Tibero, Catalog.Get(id, 6, Dbms.Tibero).Header.Dbms));
    }

    [Theory]
    [InlineData("SYSTEM", true)]
    [InlineData("system", true)]
    [InlineData("USERS", false)]
    [InlineData(null, false)]
    public void Recycle_bin_note_says_permanent_only_for_the_system_tablespace(string? tablespace, bool permanent)
    {
        var note = Dialect.RecycleBinNote(tablespace);

        Assert.StartsWith(" ", note, StringComparison.Ordinal);
        Assert.Equal(permanent, note.Contains("SYSTEM 테이블스페이스", StringComparison.Ordinal));
        Assert.Equal(!permanent, note.Contains("FLASHBACK TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_consistency_hint_compares_the_estimate_with_undo_retention()
    {
        Assert.Equal("undo_retention", Dialect.ReadConsistencyParameter);

        Assert.Equal(CheckLevel.Pass, Dialect.ReadConsistencyHint(100, null).Level);
        Assert.Equal(CheckLevel.Pass, Dialect.ReadConsistencyHint(900, 900).Level);

        var (level, message) = Dialect.ReadConsistencyHint(1_200, 900);
        Assert.Equal(CheckLevel.Confirm, level);
        Assert.Contains("1,200", message, StringComparison.Ordinal);
        Assert.Contains("900", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ORA-", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_record_error_is_the_tibero_number_not_an_ora_number()
    {
        Assert.Equal("TBR-10009", Dialect.ChildRecordFoundError);
    }

    /// <summary>Core 의 사전 점검·실행기가 묻는 모든 <c>copy.*</c> id(+ 방언 점검 목록)가 Tibero 카탈로그에 있고 Tibero 파일이다.</summary>
    [Fact]
    public void Every_copy_id_the_core_asks_for_resolves_to_a_tibero_file()
    {
        var used = CoreCopyIds().Concat(Dialect.NotRestoredProbes.Select(p => p.CatalogId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(used.Count >= 19, "Core 소스에서 copy.* id 를 충분히 찾지 못했다: " + used.Count);

        var missing = used.Where(id => !Catalog.Entries.Any(e => e.Id == id && e.Dbms == Dbms.Tibero)).ToList();
        Assert.True(missing.Count == 0, "Tibero 카탈로그에 없는 copy.* id: " + string.Join(", ", missing));

        foreach (var id in used)
        {
            var query = Catalog.Get(id, 6, Dbms.Tibero);
            Assert.StartsWith("SELECT", query.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("@PFX@", query.Sql, StringComparison.Ordinal); // 사전 점검은 접두어를 바꾸지 않고 문장을 그대로 보낸다
            Assert.True(CopyBinds(id).SetEquals(Binds(query.Sql)), id + ": 바인드 이름이 기대와 다르다");
        }
    }

    [Fact]
    public void Copy_files_that_differ_from_oracle_use_tibero_dictionary_columns()
    {
        // TM0b 실측 3.9: all_tab_privs·all_col_privs 에는 table_schema 가 없고 owner, all_triggers 에는 base_object_type 이 없다, v$parameter 대신 v$parameters.
        foreach (var id in new[] { "copy.table_privs", "copy.other_grantor_count", "copy.col_priv_count", "copy.trigger_count_all", "copy.parameter_value" })
        {
            var sql = Catalog.Get(id, 6, Dbms.Tibero).Sql;
            Assert.DoesNotContain("table_schema", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("base_object_type", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("V$PARAMETER ", sql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("v$parameters", Catalog.Get("copy.parameter_value", 6, Dbms.Tibero).Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identity_cols_answers_none_with_the_oracle_columns_without_reading_a_dictionary_view()
    {
        var sql = Catalog.Get("copy.identity_cols", 6, Dbms.Tibero).Sql;

        Assert.DoesNotContain("ALL_TAB_IDENTITY_COLS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS COLUMN_NAME", sql, StringComparison.Ordinal);
        Assert.Contains("AS GENERATION_TYPE", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE 1 = 0", sql, StringComparison.Ordinal);
    }

    /// <summary>실제 Oracle 판에 있는 id 를 Tibero 판이 같은 바인드·같은 열 수로 따르는지는 IT 가 서버로 본다. 여기서는 Oracle 에만 있는 id 가 예상한 것뿐인지 확인한다.</summary>
    [Fact]
    public void Ids_that_only_oracle_has_are_exactly_the_fga_probe_and_the_recreate_step_selects()
    {
        var oracle = new SqlCatalog(Oranda.Oracle.Catalog.EmbeddedCatalog.ReadSources(typeof(Oranda.Oracle.OracleAssembly).Assembly));
        var oracleCopy = oracle.Entries.Where(e => e.Id.StartsWith("copy.", StringComparison.Ordinal)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var tiberoCopy = Catalog.Entries.Where(e => e.Id.StartsWith("copy.", StringComparison.Ordinal)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(tiberoCopy.Except(oracleCopy)); // Tibero 에 Oracle 에 없는 copy.* 가 생기면 안 된다
        Assert.Equal(
            ["copy.fga_policy_count", "copy.recreate_grants", "copy.recreate_recycle_bin", "copy.recreate_referencing_fks", "copy.recreate_source_fks",
             "copy.recreate_trigger_ddl", "copy.recreate_triggers"],
            oracleCopy.Except(tiberoCopy).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>각 id 가 Oracle 판과 같은 바인드 이름 집합을 쓴다(호출 쪽이 이름으로 보낸다).</summary>
    private static HashSet<string> CopyBinds(string id)
    {
        var oracle = new SqlCatalog(Oranda.Oracle.Catalog.EmbeddedCatalog.ReadSources(typeof(Oranda.Oracle.OracleAssembly).Assembly));
        return Binds(oracle.Get(id, 19, Dbms.Oracle).Sql);
    }

    private static HashSet<string> Binds(string sql)
        => BindName().Matches(Regex.Replace(sql, "'[^']*'", string.Empty)).Select(m => m.Groups[1].Value.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    private static List<string> CoreCopyIds()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "Oranda.Core", "Transfer", "Copy");
        return [.. Directory.EnumerateFiles(directory, "*.cs")
            .SelectMany(f => CopyId().Matches(File.ReadAllText(f)).Select(m => m.Value.Trim('"')))
            .Distinct(StringComparer.Ordinal)];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }

    [GeneratedRegex("\"copy\\.[a-z_]+\"", RegexOptions.CultureInvariant)]
    private static partial Regex CopyId();

    [GeneratedRegex(@":([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex BindName();
}
