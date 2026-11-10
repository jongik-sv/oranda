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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recreate_difference_note_names_the_lost_deferrable_fk_and_none_of_the_unconfirmed_oracle_options(bool hasDomain)
    {
        var note = Dialect.RecreateDifferenceNote(hasDomain);

        Assert.StartsWith(" 원본과 다르게 만들어지는 것:", note, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE", note, StringComparison.Ordinal);
        Assert.Contains("FK", note, StringComparison.Ordinal); // 카탈로그에 DEFERRABLE 이 없어 FK 도 즉시 검사로 돌아온다
        Assert.EndsWith(".", note, StringComparison.Ordinal);
        foreach (var oracleOnly in new[] { "DEFAULT ON NULL", "IDENTITY", "COLLATE", "도메인" })
        {
            Assert.DoesNotContain(oracleOnly, note, StringComparison.Ordinal);
        }
    }

    /// <summary>TB4 뒤로 막지 않는다(받아 두기·되살리기·휴지통 안내는 <c>TiberoRecreateStepIt</c> 가 서버로 잰다).</summary>
    [Fact]
    public void Recreate_stays_blocked_with_a_reason_until_the_runner_copy_it()
    {
        Assert.Equal(TiberoCopyPrecheckDialect.RecreateBlocked, Dialect.RecreateBlockedReason(null!));
        Assert.False(string.IsNullOrWhiteSpace(TiberoCopyPrecheckDialect.RecreateBlocked));
    }

    /// <summary>「없으면 만들기」는 푼다(XC8b, D11 변경): Tibero 대상에 새 표를 만든다. 다시 만들기는 그대로 막는다.</summary>
    [Fact]
    public void Create_if_missing_is_not_blocked_but_recreate_still_is()
    {
        ICopyPrecheckDialect dialect = Dialect;

        Assert.Null(dialect.CreateBlockedReason(null!));
        Assert.NotNull(dialect.RecreateBlockedReason(null!));
        Assert.Equal(TiberoCopyPrecheckDialect.RecreateBlocked, dialect.RecreateBlockedReason(null!));
    }

    /// <summary>
    /// Tibero 카탈로그 모양의 표(문자 열 형식 이름 VARCHAR·CHAR, 길이 = 글자 수, 서버가 지은 제약 이름 <c>&lt;소유자&gt;_CON&lt;숫자&gt;</c>)를 같은 Tibero 에 만드는 문장.
    /// 방언의 서버 이름 판정이 실행기·미리 보기와 같게 쓰여 서버 이름은 명시하지 않고, 길이는 글자 단위(CHAR)다.
    /// </summary>
    [Fact]
    public void Tibero_catalog_shaped_table_is_created_in_another_account_with_char_lengths_and_without_server_named_constraints()
    {
        var table = new TableInfo(new DbObjectRef("FILM_USER", "ORT_T", "TABLE"),
        [
            new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
            new ColumnInfo("NM", 2, "VARCHAR") { Length = 20, DefaultValue = "'x'" },
            new ColumnInfo("CD", 3, "CHAR") { Length = 5 },
            new ColumnInfo("NN", 4, "NVARCHAR") { Length = 10 },
            new ColumnInfo("AMT", 5, "NUMBER") { Precision = 12, Scale = 2 },
            new ColumnInfo("TS", 6, "TIMESTAMP(6)"),
            new ColumnInfo("DOC", 7, "CLOB"),
        ])
        {
            Comment = "테이블 주석",
            Constraints =
            [
                new ConstraintInfo("FILM_USER_CON123", ConstraintKind.PrimaryKey, ["ID"]),
                new ConstraintInfo("FILM_USER_CON124", ConstraintKind.Check, ["AMT"]) { Condition = "AMT > 0" },
                new ConstraintInfo("CK_NM", ConstraintKind.Check, ["NM"]) { Condition = "LENGTH(NM) > 0" },
            ],
            Indexes =
            [
                new IndexInfo("FILM_USER_CON123", true, ["ID"]) { IndexType = "NORMAL" },
                new IndexInfo("IX_NM", false, ["NM"]) { IndexType = "NORMAL" },
                new IndexInfo("IX_FN", false, ["SYS_NC0001$"]) { IndexType = "FUNCTION-BASED NORMAL" },
            ],
        };

        var built = CrossTableMap.Build(Dbms.Tibero, Dbms.Tibero, table, "FILM_CMN_USER", new HashSet<string>(StringComparer.Ordinal),
            name => Dialect.IsServerName(name, "FILM_USER"));

        Assert.Equal(
        [
            "CREATE TABLE \"FILM_CMN_USER\".\"ORT_T\" (\"ID\" NUMBER(10) NOT NULL, \"NM\" VARCHAR2(20 CHAR) DEFAULT 'x', \"CD\" CHAR(5 CHAR), \"NN\" NVARCHAR2(10),"
            + " \"AMT\" NUMBER(12,2), \"TS\" TIMESTAMP(6), \"DOC\" CLOB, PRIMARY KEY (\"ID\"), CHECK (AMT > 0), CONSTRAINT \"CK_NM\" CHECK (LENGTH(NM) > 0))",
            "CREATE INDEX \"FILM_CMN_USER\".\"IX_NM\" ON \"FILM_CMN_USER\".\"ORT_T\" (\"NM\")",
            "COMMENT ON TABLE \"FILM_CMN_USER\".\"ORT_T\" IS '테이블 주석'",
        ], built.Statements);
        Assert.Empty(built.Notes); // 같은 DBMS: 달라지는 것 없음
        Assert.Empty(built.OmittedConstraints);

        // 사전 점검의 이름 겹침 검사가 보는 이름도 같은 판정이다: 서버 이름은 빠진다.
        var (constraints, indexes) = CreateTableDdl.NamedObjects(table, name => Dialect.IsServerName(name, "FILM_USER"));
        Assert.Equal(["CK_NM"], constraints);
        Assert.Equal(["IX_NM"], indexes);
    }

    /// <summary>
    /// 다시 만들기 단계(<see cref="TiberoRecreateStep"/>)가 쓰는 5개 id 가 Tibero 파일로 있고 Oracle 판과 바인드 이름·열 수가 같다(호출 쪽이 이름으로 보내고 위치로 읽는다).
    /// 이 id 들은 Core 가 아니라 DBMS 단계가 쓰므로 Core 소스 훑기(<see cref="Every_copy_id_the_core_asks_for_resolves_to_a_tibero_file"/>)에 잡히지 않는다.
    /// </summary>
    [Theory]
    [InlineData("copy.recreate_triggers", 3)]
    [InlineData("copy.recreate_grants", 3)]
    [InlineData("copy.recreate_referencing_fks", 9)]
    [InlineData("copy.recreate_source_fks", 10)]
    [InlineData("copy.recreate_recycle_bin", 1)]
    public void Recreate_step_ids_are_tibero_files_with_the_oracle_binds_and_column_count(string id, int columns)
    {
        var oracle = new SqlCatalog(Oranda.Oracle.Catalog.EmbeddedCatalog.ReadSources(typeof(Oranda.Oracle.OracleAssembly).Assembly)).Get(id, 19, Dbms.Oracle);
        var tibero = Catalog.Get(id, 6, Dbms.Tibero);

        Assert.Equal(Dbms.Tibero, tibero.Header.Dbms);
        Assert.StartsWith("SELECT", tibero.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@PFX@", tibero.Sql, StringComparison.Ordinal);
        Assert.True(Binds(oracle.Sql).SetEquals(Binds(tibero.Sql)), id + ": 바인드 이름이 Oracle 판과 다르다");
        Assert.Equal(columns, SelectListCount(oracle.Sql));
        Assert.Equal(columns, SelectListCount(tibero.Sql));

        // TM0b 3.5·3.7·3.9: Tibero 사전 뷰에 없는 열을 쓰지 않는다.
        foreach (var missing in new[] { "base_object_type", "table_schema", "operation", "c.deferrable", "c.deferred" })
        {
            Assert.DoesNotContain(missing, tibero.Sql, StringComparison.OrdinalIgnoreCase);
        }
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

    [Fact]
    public void Trigger_status_is_a_tibero_only_file_with_the_owner_and_name_binds_and_one_column()
    {
        var entry = Catalog.Get("copy.trigger_status", 6, Dbms.Tibero);

        Assert.Equal(Dbms.Tibero, entry.Header.Dbms);
        Assert.StartsWith("SELECT", entry.Sql, StringComparison.Ordinal);
        Assert.True(new HashSet<string>(["OWN", "NAME"], StringComparer.Ordinal).SetEquals(Binds(entry.Sql)));
        Assert.Equal(1, SelectListCount(entry.Sql));
        Assert.Equal(entry.Sql, ((ICopyDialect)TiberoCopyDialect.Instance).TriggerStatusSql);
    }

    /// <summary>
    /// 실제 Oracle 판에 있는 id 를 Tibero 판이 같은 바인드·같은 열 수로 따르는지는 IT 가 서버로 본다. 여기서는 Oracle 에만 있는 id 가 예상한 것뿐인지 확인한다:
    /// FGA 점검(사전 뷰 없음)과 트리거 DDL(GET_DDL('TRIGGER') = -14001, Tibero 는 메타데이터 제공자로 조립).
    /// </summary>
    [Fact]
    public void Ids_that_only_oracle_has_are_exactly_the_fga_probe_and_the_trigger_ddl()
    {
        var oracle = new SqlCatalog(Oranda.Oracle.Catalog.EmbeddedCatalog.ReadSources(typeof(Oranda.Oracle.OracleAssembly).Assembly));
        var oracleCopy = oracle.Entries.Where(e => e.Id.StartsWith("copy.", StringComparison.Ordinal)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var tiberoCopy = Catalog.Entries.Where(e => e.Id.StartsWith("copy.", StringComparison.Ordinal)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        // Tibero 에 Oracle 에 없는 copy.* 는 copy.trigger_status 하나뿐이다: 컴파일 오류 트리거를 오류 없이 받는 서버에서만 쓰는 조회로,
        // Oracle 은 ORA-24344 로 알려 주므로 쓰지 않는다(Oracle 카탈로그는 바꾸지 않는다). Core 는 이 id 를 모르고 방언(TriggerStatusSql)이 글을 넘긴다.
        Assert.Equal(["copy.trigger_status"], tiberoCopy.Except(oracleCopy).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["copy.fga_policy_count", "copy.recreate_trigger_ddl"], oracleCopy.Except(tiberoCopy).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>첫 <c>SELECT</c> 와 같은 깊이의 첫 <c>FROM</c> 사이 선택 목록 항목 수(괄호·따옴표 안의 쉼표는 세지 않는다).</summary>
    private static int SelectListCount(string sql)
    {
        var start = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) + "SELECT".Length;
        var depth = 0;
        var quoted = false;
        var count = 1;
        for (var i = start; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch == '(')
            {
                depth++;
            }
            else if (!quoted && ch == ')')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && ch == ',')
            {
                count++;
            }
            else if (!quoted && depth == 0 && string.Compare(sql, i, " FROM ", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return count;
            }
        }

        throw new InvalidOperationException("FROM 을 찾지 못했다");
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
