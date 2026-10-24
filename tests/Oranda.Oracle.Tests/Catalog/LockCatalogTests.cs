using System.Text.RegularExpressions;
using Oranda.Core.Catalog;
using Oranda.Oracle.Catalog;

namespace Oranda.Oracle.Tests.Catalog;

// 소유: TSK-05-02. lock/ 폴더 카탈로그: id 존재·11g 선택, 팩 불필요, gv$ 사용, SELECT 전용(DML·임시 표·쓰기 패키지 없음), 바인드 이름.
[Trait("Category", "Catalog")]
public partial class LockCatalogTests
{
    private static readonly SqlCatalog Catalog = EmbeddedCatalog.Load();

    private static readonly string[] ElevenIds =
        ["lock.list", "lock.blocker_impact", "lock.locked_row", "lock.ddl_locks", "lock.waiting_binds", "lock.idle_transactions"];

    [Theory]
    [InlineData("lock.list")]
    [InlineData("lock.blocker_impact")]
    [InlineData("lock.locked_row")]
    [InlineData("lock.ddl_locks")]
    [InlineData("lock.waiting_binds")]
    [InlineData("lock.idle_transactions")]
    public void Query_is_selected_for_11_19_and_26(string id)
    {
        Assert.Equal(11, Catalog.Get(id, 11).Header.MinVersion);
        Assert.Equal(id, Catalog.Get(id, 19).Header.Id);
        Assert.Equal(id, Catalog.Get(id, 26).Header.Id);
        Assert.Same(Catalog.Get(id, 11), Catalog.Get(id, 26)); // 한 파일이 모든 버전에서 돈다(FETCH FIRST·con_id 없음)
    }

    /// <summary>v$diag_alert_ext 는 12.2 이후. 11.2 에는 변형이 없고 서비스가 빈 목록을 돌려준다.</summary>
    [Fact]
    public void Deadlocks_read_alert_log_only_from_19()
    {
        Assert.Throws<CatalogQueryNotFoundException>(() => Catalog.Get("lock.deadlocks", 11));
        var query = Catalog.Get("lock.deadlocks", 19);
        Assert.Contains("v$diag_alert_ext", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORA-00060", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ROWNUM <= :max", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Lock_queries_need_no_license_pack_and_avoid_pack_views()
    {
        var headers = Catalog.Entries.Where(h => h.Id.StartsWith("lock.", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, headers.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var header in headers)
        {
            Assert.Equal(CatalogLicense.None, header.License);
            var sql = Catalog.Get(header.Id, 26).Sql;
            foreach (var view in new[] { "active_session_history", "dba_hist_", "sql_monitor", "sqltune", "advisor" })
            {
                Assert.DoesNotContain(view, sql, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>업무 PC 의 결재 도구가 잡는 문장(DML·DDL·임시 표·쓰기 패키지·잠금)이 조회 경로에 없어야 한다.</summary>
    [Fact]
    public void Lock_queries_are_plain_selects_without_dml_or_write_packages()
    {
        foreach (var header in Catalog.Entries.Where(h => h.Id.StartsWith("lock.", StringComparison.Ordinal)))
        {
            var sql = Catalog.Get(header.Id, header.MinVersion).Sql;
            var body = StripComments(sql);

            Assert.StartsWith("SELECT", body.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.False(WriteWord().IsMatch(body), $"{header.Path}: {WriteWord().Match(body).Value}");
            Assert.DoesNotContain(";", body, StringComparison.Ordinal);
            foreach (Match m in PackageCall().Matches(body))
            {
                Assert.Equal("DBMS_ROWID.ROWID_CREATE", m.Value.ToUpperInvariant()); // 읽기 전용 함수만
            }
        }
    }

    [Fact]
    public void Instance_wide_queries_use_gv_views()
    {
        foreach (var id in new[] { "lock.list", "lock.blocker_impact", "lock.locked_row", "lock.waiting_binds", "lock.idle_transactions" })
        {
            var query = Catalog.Get(id, 19);
            Assert.Equal(CatalogRac.Gv, query.Header.Rac);
            Assert.Contains("gv$", query.Sql, StringComparison.OrdinalIgnoreCase);
        }

        // dba_ddl_locks·알림 로그는 인스턴스 하나만 본다(gv 짝 없음). 인스턴스 번호는 SYS_CONTEXT 로 채운다.
        foreach (var id in new[] { "lock.ddl_locks", "lock.deadlocks" })
        {
            var query = Catalog.Get(id, 26);
            Assert.Equal(CatalogRac.V, query.Header.Rac);
            Assert.Contains("SYS_CONTEXT('USERENV', 'INSTANCE')", query.Sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("lock.list", "inst_id,max_rows,min_wait,schema,types,username,waiting_only")]
    [InlineData("lock.blocker_impact", "")]
    [InlineData("lock.locked_row", "inst_id,serial,sid")]
    [InlineData("lock.ddl_locks", "")]
    [InlineData("lock.waiting_binds", "child,inst_id,serial,sid,sql_id")]
    [InlineData("lock.idle_transactions", "min_idle")]
    [InlineData("lock.deadlocks", "max")]
    public void Queries_bind_the_names_the_service_adds(string id, string expected)
    {
        var binds = BindName().Matches(StripComments(Catalog.Get(id, 26).Sql)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(',', binds.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void List_joins_objects_and_transactions_and_excludes_background_locks()
    {
        var sql = Catalog.Get("lock.list", 11).Sql;

        Assert.Contains("FROM gv$lock l", sql, StringComparison.Ordinal);
        Assert.Contains("@PFX@_objects o ON x.type = 'TM' AND o.object_id = x.id1", sql, StringComparison.Ordinal);
        Assert.Contains("t.xidusn = TRUNC(x.id1 / 65536) AND t.xidslot = MOD(x.id1, 65536) AND t.xidsqn = x.id2", sql, StringComparison.Ordinal);
        // gv$lock 에 요청 행이 없는 큐 대기는 대기 인자(p1 모드, p2·p3 = id1·id2)로 채운다.
        Assert.Contains("SUBSTR(q.event, 6, 2), q.p2, q.p3, 0, BITAND(q.p1, 65535)", sql, StringComparison.Ordinal);
        Assert.Contains("q.event LIKE 'enq: __ - %'", sql, StringComparison.Ordinal);
        Assert.Contains("s.type <> 'BACKGROUND'", sql, StringComparison.Ordinal);
        foreach (var bg in new[] { "'MR'", "'AE'", "'TO'", "'RT'", "'XR'", "'CF'" })
        {
            Assert.Contains(bg, sql, StringComparison.Ordinal);
        }

        Assert.EndsWith("WHERE ROWNUM <= :max_rows", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (SELECT", sql, StringComparison.OrdinalIgnoreCase); // 큰 목록도 조인으로(IN 1000개 제한 없음)
    }

    [Fact]
    public void Locked_row_builds_rowid_from_data_object_and_relative_file_only_for_row_lock_waits()
    {
        var sql = Catalog.Get("lock.locked_row", 11).Sql;

        Assert.Contains("DBMS_ROWID.ROWID_CREATE(1, o.data_object_id, NVL(f.rfile#, s.row_wait_file#), s.row_wait_block#, s.row_wait_row#)", sql, StringComparison.Ordinal);
        Assert.Contains("v$datafile f ON f.file# = s.row_wait_file#", sql, StringComparison.Ordinal);
        Assert.Contains("s.event = 'enq: TX - row lock contention'", sql, StringComparison.Ordinal);
        Assert.Contains("s.row_wait_obj# > 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Idle_and_ddl_lists_are_capped_with_rownum()
    {
        Assert.EndsWith("WHERE ROWNUM <= 500", Catalog.Get("lock.idle_transactions", 11).Sql, StringComparison.Ordinal);
        Assert.EndsWith("WHERE ROWNUM <= 1000", Catalog.Get("lock.ddl_locks", 11).Sql, StringComparison.Ordinal);
        Assert.Contains("s.status = 'INACTIVE'", Catalog.Get("lock.idle_transactions", 11).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Eleven_queries_use_no_12c_features()
    {
        foreach (var id in ElevenIds)
        {
            Assert.Empty(CatalogParser.FindPre12cFeatures(Catalog.Get(id, 11).Sql));
        }
    }

    private static string StripComments(string sql) => LineComment().Replace(sql, string.Empty);

    [GeneratedRegex(@":([a-z_]+)")]
    private static partial Regex BindName();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|ALTER|DROP|GRANT|REVOKE|LOCK\s+TABLE|COMMIT|ROLLBACK|BEGIN|DECLARE|EXECUTE|CALL|GLOBAL\s+TEMPORARY|FOR\s+UPDATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteWord();

    [GeneratedRegex(@"\b(DBMS|UTL|SYS)_?\w*\.\w+", RegexOptions.IgnoreCase)]
    private static partial Regex PackageCall();
}
