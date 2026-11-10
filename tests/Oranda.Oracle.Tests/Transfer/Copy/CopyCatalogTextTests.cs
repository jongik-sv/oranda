using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: TP0b-1 커밋 A, TP0b-2 커밋 A(실행 경로 SELECT 추가). 사전 점검·실행 경로 SELECT 글을 C# 상수에서 Catalog/copy/*.sql 로 옮긴 뒤 글자·바인드 이름이 그대로인지(DB 없음).
// 기대값은 옮기기 전 상수(CopyCatalogQueries.cs·CopyPrecheckService.cs)를 글자 그대로 옮겨 적은 것이다.

[Trait("Category", "Catalog")]
public sealed partial class CopyCatalogTextTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    /// <summary>Oracle 사전 점검(<c>OracleCopyPrecheckService</c>)이 넘기는 카탈로그. 서버 버전으로 고른다(copy.* 는 11 이상 한 파일).</summary>
    private static readonly ISqlCatalog ServiceCatalog = new OracleSqlCatalog();

    private static readonly ServerInfo Server19 =
        new("19.0.0.0.0", "ORCL", "h", "PDB1", "ORCL", "PDB1", true, 1, new ServerCapabilities(false, true, true, false, false));

    /// <summary>id → (옮기기 전 상수 글, 바인드 이름 순서대로).</summary>
    private static readonly Dictionary<string, (string Sql, string[] Binds)> PrecheckPrevious = new(StringComparer.Ordinal)
    {
        // CopyCatalogQueries.cs
        ["copy.session_user"] = ("SELECT USER FROM DUAL", []),
        ["copy.session_roles"] = ("SELECT ROLE FROM SESSION_ROLES", []),
        ["copy.session_privs"] = ("SELECT PRIVILEGE FROM SESSION_PRIVS", []),
        ["copy.table_privs"] = (
            "SELECT GRANTEE, PRIVILEGE FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :OWN AND TABLE_NAME = :TBL", ["OWN", "TBL"]),
        ["copy.enabled_triggers"] = (
            "SELECT OWNER, TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :OWN AND TABLE_NAME = :TBL AND STATUS = 'ENABLED' ORDER BY OWNER, TRIGGER_NAME",
            ["OWN", "TBL"]),
        ["copy.referencing_fks"] = (
            "SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME FROM ALL_CONSTRAINTS c "
            + "JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME "
            + "WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND p.OWNER = :OWN AND p.TABLE_NAME = :TBL "
            + "AND NOT (c.OWNER = p.OWNER AND c.TABLE_NAME = p.TABLE_NAME) "
            + "ORDER BY c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME",
            ["OWN", "TBL"]),
        ["copy.identity_cols"] = (
            "SELECT COLUMN_NAME, GENERATION_TYPE FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = :OWN AND TABLE_NAME = :TBL", ["OWN", "TBL"]),
        ["copy.sequence_info"] = (
            "SELECT LAST_NUMBER, INCREMENT_BY FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ", ["OWN", "SEQ"]),
        ["copy.sequence_names"] = (
            "SELECT SEQUENCE_NAME FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN ORDER BY SEQUENCE_NAME", ["OWN"]),
        ["copy.parameter_value"] = ("SELECT VALUE FROM V$PARAMETER WHERE NAME = :PNAME", ["PNAME"]),

        // CopyPrecheckService.cs
        ["copy.tablespace"] = ("SELECT tablespace_name FROM all_tables WHERE owner = :OWN AND table_name = :TBL", ["OWN", "TBL"]),
        ["copy.constraint_owner"] = ("SELECT table_name FROM all_constraints WHERE owner = :OWN AND constraint_name = :NAME", ["OWN", "NAME"]),
        ["copy.index_owner"] = ("SELECT table_name FROM all_indexes WHERE owner = :OWN AND index_name = :NAME", ["OWN", "NAME"]),
        ["copy.col_priv_count"] = ("SELECT COUNT(*) FROM all_col_privs WHERE table_schema = :OWN AND table_name = :TBL", ["OWN", "TBL"]),
        ["copy.other_grantor_count"] = (
            "SELECT COUNT(*) FROM all_tab_privs WHERE table_schema = :OWN AND table_name = :TBL AND grantor <> :OWN", ["OWN", "TBL", "OWN"]),
        ["copy.mview_log_count"] = ("SELECT COUNT(*) FROM all_mview_logs WHERE log_owner = :OWN AND master = :TBL", ["OWN", "TBL"]),
        ["copy.vpd_policy_count"] = ("SELECT COUNT(*) FROM all_policies WHERE object_owner = :OWN AND object_name = :TBL", ["OWN", "TBL"]),
        ["copy.fga_policy_count"] = ("SELECT COUNT(*) FROM all_audit_policies WHERE object_schema = :OWN AND object_name = :TBL", ["OWN", "TBL"]),
        ["copy.object_exists"] = ("SELECT COUNT(*) FROM all_objects WHERE owner = :OWN AND object_name = :NAME", ["OWN", "NAME"]),
        ["copy.trigger_count_all"] = (
            "SELECT COUNT(*) FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE'", ["OWN", "TBL"]),
        ["copy.target_is_table"] = (
            "SELECT COUNT(*) FROM all_tables t WHERE t.owner = :OWN AND t.table_name = :TBL"
            + " AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = t.owner AND m.mview_name = t.table_name)",
            ["OWN", "TBL"]),
    };

    /// <summary>TP0b-2 커밋 A: 실행 경로(CopyRunner.cs·RecreateStep.cs)의 SELECT. 옮기기 전 C# 상수를 글자 그대로 옮겨 적었다.</summary>
    private static readonly Dictionary<string, (string Sql, string[] Binds)> RunnerPrevious = new(StringComparer.Ordinal)
    {
        // CopyRunner.cs
        ["copy.table_exists"] = ("SELECT COUNT(*) FROM all_tables WHERE owner = :OWNER AND table_name = :NAME", ["OWNER", "NAME"]),
        ["copy.sequence_increment"] = (
            "SELECT increment_by, last_number, cache_size FROM all_sequences WHERE sequence_owner = :OWNER AND sequence_name = :NAME",
            ["OWNER", "NAME"]),
        ["copy.fk_columns"] = (
            "SELECT cc.column_name, pc.column_name, p.owner, p.table_name"
            + " FROM all_constraints c"
            + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
            + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
            + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
            + " WHERE c.owner = :OWNER AND c.constraint_name = :NAME"
            + " ORDER BY cc.position",
            ["OWNER", "NAME"]),

        // RecreateStep.cs
        ["copy.recreate_triggers"] = (
            "SELECT owner, trigger_name, status FROM all_triggers"
            + " WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE' ORDER BY trigger_name",
            ["OWN", "TBL"]),
        ["copy.recreate_trigger_ddl"] = ("SELECT DBMS_METADATA.GET_DDL('TRIGGER', :NAME, :OWN) FROM DUAL", ["NAME", "OWN"]),
        ["copy.recreate_grants"] = (
            "SELECT grantee, privilege, grantable FROM all_tab_privs"
            + " WHERE table_schema = :OWN AND table_name = :TBL AND grantor = :OWN"
            + " ORDER BY grantee, privilege",
            ["OWN", "TBL", "OWN"]),
        ["copy.recreate_referencing_fks"] = (
            "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred"
            + " FROM all_constraints c"
            + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
            + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
            + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
            + " WHERE c.constraint_type = 'R' AND p.owner = :OWN AND p.table_name = :TBL"
            + " AND NOT (c.owner = p.owner AND c.table_name = p.table_name)"
            + " ORDER BY c.owner, c.table_name, c.constraint_name, cc.position",
            ["OWN", "TBL"]),
        ["copy.recreate_source_fks"] = (
            "SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred, p.table_name"
            + " FROM all_constraints c"
            + " JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name"
            + " JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name"
            + " JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position"
            + " WHERE c.constraint_type = 'R' AND c.owner = :OWN AND c.table_name = :TBL AND p.owner = c.owner"
            + " ORDER BY c.constraint_name, cc.position",
            ["OWN", "TBL"]),
        ["copy.recreate_recycle_bin"] = (
            "SELECT object_name FROM user_recyclebin WHERE original_name = :TBL AND type = 'TABLE' AND operation = 'DROP'", ["TBL"]),

        // CopyWaitProbe.cs(WH1): 옮기기 전 상수가 없는 새 조회라 파일 글을 그대로 적는다.
        ["copy.wait_cause"] = (
            "SELECT s.EVENT, s.WAIT_CLASS, CASE WHEN s.STATE = 'WAITING' THEN TRUNC(s.WAIT_TIME_MICRO / 1000000) ELSE 0 END AS SECONDS_IN_WAIT,"
            + " s.BLOCKING_SESSION, b.USERNAME, b.PROGRAM, b.MACHINE"
            + " FROM GV$SESSION s JOIN GV$INSTANCE i ON i.INST_ID = s.INST_ID"
            + " LEFT JOIN GV$SESSION b ON b.INST_ID = s.BLOCKING_INSTANCE AND b.SID = s.BLOCKING_SESSION"
            + " WHERE s.SID = :SID AND i.INSTANCE_NAME = NVL(:INST, SYS_CONTEXT('USERENV', 'INSTANCE_NAME'))",
            ["SID", "INST"]),
    };

    private static readonly Dictionary<string, (string Sql, string[] Binds)> Previous =
        PrecheckPrevious.Concat(RunnerPrevious).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>
    /// 실제로 쓰는 경로: 사전 점검·실행기(TP0b-2 커밋 B 부터) 모두 Core 의 <see cref="CopyCatalogQueries.Sql"/>(Oracle 카탈로그·서버 버전).
    /// Oracle 에 남은 <c>RecreateStep</c> 은 아직 <see cref="EmbeddedCatalog.Text"/> 로 읽는다(<see cref="RecreateStepIds"/>).
    /// </summary>
    private static string Actual(string id) => CopyCatalogQueries.Sql(ServiceCatalog, Server19, id);

    /// <summary>Oracle <c>RecreateStep.cs</c> 가 <see cref="EmbeddedCatalog.Text"/> 로 읽는 id.</summary>
    private static readonly string[] RecreateStepIds =
    [
        "copy.recreate_triggers", "copy.recreate_trigger_ddl", "copy.recreate_grants", "copy.recreate_referencing_fks",
        "copy.recreate_source_fks", "copy.recreate_recycle_bin",
    ];

    // OracleCopyPrecheckDialect.cs: 되살리지 않는 것 조회 id(NotRestoredProbes) 가 사전 점검 서비스에서 방언으로 옮겨 갔다(TP0b-1 커밋 B).
    // 사전 점검 본문은 Core, Oracle 방언만 Oracle 에 있다(TP0b-1 C). 경로는 리포 뿌리 기준.
    private static readonly string[] Sources =
    [
        "src/Oranda.Core/Transfer/Copy/CopyCatalogQueries.cs",
        "src/Oranda.Core/Transfer/Copy/CopyPrecheckService.cs",
        "src/Oranda.Oracle/Transfer/Copy/OracleCopyPrecheckDialect.cs",
        "src/Oranda.Core/Transfer/Copy/CopyRunner.cs",
        "src/Oranda.Oracle/Transfer/Copy/RecreateStep.cs",
        "src/Oranda.Core/Transfer/Copy/CopyWaitProbe.cs",
    ];

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var id in Previous.Keys)
        {
            data.Add(id);
        }

        return data;
    }

    /// <summary>copy.* 의 최소 버전. copy.identity_cols 만 12(ALL_TAB_IDENTITY_COLS 는 12c 부터, 11g 에는 IDENTITY 열이 없어 보내지 않는다), 나머지는 11.</summary>
    private static int MinVersionOf(string id) => id == "copy.identity_cols" ? 12 : 11;

    [Theory]
    [MemberData(nameof(Ids))]
    public void Catalog_text_equals_previous_literal(string id)
    {
        var expected = Previous[id].Sql;

        Assert.Equal(expected, Actual(id)); // 서비스가 실제로 쓰는 경로
        if (RecreateStepIds.Contains(id, StringComparer.Ordinal))
        {
            Assert.Equal(expected, EmbeddedCatalog.Text(id)); // Oracle RecreateStep 의 경로
        }
        Assert.Equal(expected, Catalog.Get(id, MinVersionOf(id)).Sql);
        Assert.Same(Catalog.Get(id, MinVersionOf(id)), Catalog.Get(id, 26)); // 버전 변형 없음: 최소 버전 ~ 26ai 가 한 파일
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Bind_names_are_unchanged(string id)
    {
        var binds = BindName().Matches(Actual(id)).Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(Previous[id].Binds, binds);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Header_is_select_only_without_pack_from_11(string id)
    {
        var minVersion = MinVersionOf(id);
        var query = Catalog.Get(id, minVersion);

        Assert.Equal(minVersion, query.Header.MinVersion);
        Assert.Equal(CatalogLicense.None, query.Header.License);
        Assert.Equal($"copy/{id["copy.".Length..]}.sql", query.Header.Path);
        Assert.NotEmpty(query.Header.Requires);
        Assert.StartsWith("SELECT ", query.Sql, StringComparison.Ordinal); // 대량 읽기가 첫 낱말을 본다
        Assert.DoesNotContain(";", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_copy_id_resolves_and_no_other_copy_file_exists()
    {
        var inCatalog = Catalog.Entries.Where(h => h.Id.StartsWith("copy.", StringComparison.Ordinal)).Select(h => h.Id)
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(Previous.Keys.Order(StringComparer.Ordinal).ToList(), inCatalog);
        foreach (var id in inCatalog)
        {
            Assert.NotEmpty(Actual(id));
        }
    }

    /// <summary>호출부가 쓰는 id 문자열(오타)이 모두 카탈로그에 있고, 카탈로그의 copy.* 가 모두 쓰인다.</summary>
    [Fact]
    public void Ids_used_by_precheck_sources_match_catalog()
    {
        var root = FindRepositoryRoot();
        var used = Sources
            .SelectMany(f => UsedId().Matches(File.ReadAllText(Path.Combine(root, f))).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Previous.Keys.Order(StringComparer.Ordinal).ToList(), used);
    }

    [GeneratedRegex(@"(?<![\w:]):([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindName();

    [GeneratedRegex("\"(copy\\.[a-z_]+)\"")]
    private static partial Regex UsedId();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다.");
    }
}
