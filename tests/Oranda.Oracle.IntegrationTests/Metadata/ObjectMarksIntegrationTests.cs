using System.Diagnostics;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Metadata;

/// <summary>
/// 스키마 브라우저 표식(26ai): 종류·상태가 다른 객체를 만들어 보조 질의(<c>meta.marks_*</c>)·테이블 펼침 끝 열이 맞게 가르는지 보고,
/// 목록 갱신·보조 질의 시간과 행 수를 시험 출력에 남긴다(큰 스키마 SYS·사전 권한 없는 계정 포함, 측정 보고용).
/// 환경 변수: ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER/ADMIN_PASSWORD(없으면 건너뜀, ORANDA_IT_REQUIRE=1 이면 실패). 접속 정보는 출력하지 않는다.
/// 시험 계정(ORT_MK_xxxxxx·ORT_MKA_xxxxxx)은 실행마다 만들고 끝에 지운다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ObjectMarksIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Marks_tell_kinds_and_states_apart_and_the_aux_queries_stay_light()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } admin || Env("ADMIN_PASSWORD") is not { } password)
        {
            if (Env("REQUIRE") == "1")
            {
                throw new InvalidOperationException("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            }

            output.WriteLine("건너뜀: ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            return;
        }

        var dataSource = $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";
        var sfx = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var u = "ORT_MK_" + sfx;
        var app = "ORT_MKA_" + sfx;
        var appPassword = "Ap" + Guid.NewGuid().ToString("N")[..16] + "x1";
        var adminProfile = new ConnectionProfile("t-marks", admin, dataSource);
        await using var raw = new OracleConnection(adminProfile.BuildConnectionString(password));
        await raw.OpenAsync();
        var sessions = new List<ISession>();
        try
        {
            await CreateObjectsAsync(raw, u, app, appPassword);
            var dba = await OpenAsync(adminProfile, password, sessions);

            // 1) 보조 질의 하나씩: 표식이 맞게 갈린다.
            var marks = new Dictionary<DbObjectRef, ObjectMark>();
            foreach (var id in OracleMetadataProvider.MarkIds)
            {
                var rows = await OracleMetadataProvider.QueryAsync(dba, id, c => c.Parameters.Add("owner", u), default);
                foreach (var (r, m) in ObjectMarkRows.Read(id, u, rows))
                {
                    marks[r] = m;
                }
            }

            ObjectMark? Mark(string name, string type) => marks.GetValueOrDefault(new DbObjectRef(u, name, type));
            Assert.Null(Mark("HEAP_T", "TABLE")); // 일반 테이블은 행이 오지 않는다
            Assert.Equal("GTT_SESSION", Mark("GTT_S", "TABLE")?.SubKind);
            Assert.Equal("GTT_TRANSACTION", Mark("GTT_T", "TABLE")?.SubKind);
            Assert.Equal("IOT", Mark("IOT_T", "TABLE")?.SubKind);
            Assert.Equal("CLUSTER", Mark("CL_T", "TABLE")?.SubKind);
            Assert.Equal("OBJECT", Mark("OBJ_T", "TABLE")?.SubKind);
            Assert.Equal("PART:RANGE+INTERVAL", Mark("PART_R", "TABLE")?.SubKind);
            Assert.Equal(new ObjectMark("BEFORE ROW", null), Mark("TRG_BR", "TRIGGER"));
            Assert.Equal(new ObjectMark("AFTER STATEMENT", "DISABLED"), Mark("TRG_AS", "TRIGGER"));
            Assert.Equal(new ObjectMark("INSTEAD ROW", null), Mark("TRG_IO", "TRIGGER"));
            Assert.Equal(new ObjectMark("AFTER DDL", null), Mark("TRG_DDL", "TRIGGER"));
            Assert.Equal(new ObjectMark(null, "DISABLED"), Mark("JOB_OFF", "JOB"));
            Assert.Null(Mark("JOB_ON", "JOB")); // 켜져 있고 예약됨 = 기본
            Assert.Equal("TARGET:TABLE", Mark("SYN_OK", "SYNONYM")?.SubKind);
            Assert.Equal("TARGET:PACKAGE", Mark("SYN_PKG", "SYNONYM")?.SubKind);
            Assert.Equal(new ObjectMark(null, "BROKEN"), Mark("SYN_BAD", "SYNONYM"));
            Assert.Equal("COMPLETE", Mark("MV_C", "MATERIALIZED VIEW")?.SubKind);
            Assert.Equal("STALE", Mark("MV_C", "MATERIALIZED VIEW")?.StateCode); // 바탕 표를 바꾼 뒤 갱신하지 않았다
            Assert.Equal("VARRAY", Mark("T_VA", "TYPE")?.SubKind);
            Assert.Equal("NESTED_TABLE", Mark("T_NT", "TYPE")?.SubKind);
            Assert.Null(Mark("T_PT", "TYPE")); // 객체 타입 = 기본

            // 2) 제공자 흐름: 목록을 먼저 받고(목록 질의는 그대로), 표식은 소유자마다 뒤에서 읽어 캐시에 넣고 알린다.
            var cache = new MemoryMetadataCache();
            var bus = new RecordingBus();
            var provider = new OracleMetadataProvider(cache, bus, Timeout.InfiniteTimeSpan);
            var list = Stopwatch.StartNew();
            var all = await provider.RefreshAsync(dba, default);
            output.WriteLine(Line($"[목록 갱신 dba] meta.objects 전체 {all.Count}행 {list.Elapsed.TotalSeconds:0.00}초"));
            Assert.Empty(await provider.ListObjectMarksAsync(dba, u, default));
            await WaitAsync(() => bus.Events.OfType<ObjectMarksRefreshed>().Any());
            var cached = await provider.ListObjectMarksAsync(dba, u, default);
            Assert.Equal(marks.Count, cached.Count);
            Assert.Equal("GTT_SESSION", cached[new DbObjectRef(u, "GTT_S", "TABLE")].SubKind);

            // 3) 테이블 펼침은 이미 읽는 질의의 끝 열로: 인덱스 UNUSABLE·비트맵·역방향·파티션, 제약 꺼짐·NOVALIDATE, 객체 테이블 종류.
            var heap = await provider.GetTableAsync(dba, new DbObjectRef(u, "HEAP_T", "TABLE"), default);
            var idx = heap.Indexes.ToDictionary(i => i.Name);
            Assert.True(idx["HEAP_IX"].IsUnusable);
            Assert.Equal("NORMAL/REV", idx["HEAP_RX"].IndexType);
            Assert.Contains(idx["HEAP_FX"].Expressions, e => e is not null);
            var cons = heap.Constraints.ToDictionary(c => c.Name);
            Assert.False(cons["HEAP_CK"].IsEnabled);
            Assert.False(cons["HEAP_NV"].IsValidated);
            Assert.True(cons["HEAP_PK"].IsValidated);
            var part = await provider.GetTableAsync(dba, new DbObjectRef(u, "PART_R", "TABLE"), default);
            Assert.True(part.Indexes.Single(i => i.Name == "PART_IX").IsPartitioned);
            Assert.Equal(TableKind.ObjectTable, (await provider.GetTableAsync(dba, new DbObjectRef(u, "OBJ_T", "TABLE"), default)).Kind);
            var cols = (await provider.GetTableAsync(dba, new DbObjectRef(u, "COL_T", "TABLE"), default)).Columns.ToDictionary(c => c.Name);
            Assert.True(cols["ID"].IsIdentity && cols["V"].IsVirtual && cols["H"].IsInvisible && cols["C"].DataType == "CLOB");

            // 4) 측정: 보조 질의 각각(시험 계정·큰 스키마 SYS), dba_ 와 all_(사전 권한 없는 계정).
            var appSession = await OpenAsync(new ConnectionProfile("t-marks-app", app, dataSource), appPassword, sessions);
            await MeasureAsync("dba", dba, u);
            await MeasureAsync("dba", dba, "SYS");
            await MeasureAsync("all", appSession, u);
            await MeasureAsync("all", appSession, "SYS");
            await MeasureAsync("all 두 번째", appSession, "SYS"); // 공유 풀에 문장이 있을 때(앱이 다시 읽을 때)
            var appList = Stopwatch.StartNew();
            var appAll = await new OracleMetadataProvider(new MemoryMetadataCache(), new RecordingBus(), Timeout.InfiniteTimeSpan).RefreshAsync(appSession, default);
            output.WriteLine(Line($"[목록 갱신 all] meta.objects 전체 {appAll.Count}행 {appList.Elapsed.TotalSeconds:0.00}초"));

            // 5) 참고: PUBLIC 동의어까지 대상 확인을 하면(질의는 PUBLIC 을 뺀다) 얼마나 드는가.
            await MeasurePublicSynonymsAsync(raw);
        }
        finally
        {
            foreach (var s in sessions)
            {
                await s.DisposeAsync();
            }

            foreach (var sql in new[] { $"DROP USER {u} CASCADE", $"DROP USER {app} CASCADE" })
            {
                try
                {
                    await ExecAsync(raw, sql);
                }
                catch (OracleException ex)
                {
                    output.WriteLine("정리 실패: " + ex.Message.Split('\n')[0]);
                }
            }
        }
    }

    private async Task MeasureAsync(string prefix, ISession s, string owner)
    {
        var parts = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var id in OracleMetadataProvider.MarkIds)
        {
            var timer = Stopwatch.StartNew();
            var rows = await OracleMetadataProvider.QueryAsync(s, id, c => c.Parameters.Add("owner", owner), default);
            parts.Add(Line($"{id["meta.marks_".Length..]} {rows.Count}행 {timer.Elapsed.TotalSeconds:0.000}초"));
        }

        var marksTime = total.Elapsed;

        var objects = await OracleMetadataProvider.QueryAsync(s, "meta.objects", c =>
        {
            c.Parameters.Add(new OracleParameter("owner", OracleDbType.Varchar2) { Value = owner });
            c.Parameters.Add(new OracleParameter("types", OracleDbType.Varchar2) { Value = DBNull.Value });
            c.Parameters.Add(new OracleParameter("name_pattern", OracleDbType.Varchar2) { Value = DBNull.Value });
            c.Parameters.Add(new OracleParameter("invalid_only", OracleDbType.Int32) { Value = 0 });
            c.Parameters.Add(new OracleParameter("created_after", OracleDbType.Date) { Value = DBNull.Value });
            c.Parameters.Add(new OracleParameter("modified_after", OracleDbType.Date) { Value = DBNull.Value });
            c.Parameters.Add(new OracleParameter("max_rows", OracleDbType.Int32) { Value = int.MaxValue });
        }, default);
        output.WriteLine(Line($"[표식 {prefix} {(owner == "SYS" ? "SYS" : "시험 계정")}] 객체 {objects.Count}개 · ") + string.Join(" · ", parts)
            + Line($" · 표식 모두 {marksTime.TotalSeconds:0.00}초"));
    }

    private async Task MeasurePublicSynonymsAsync(OracleConnection raw)
    {
        var timer = Stopwatch.StartNew();
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), SUM(CASE WHEN target_type IS NULL THEN 1 ELSE 0 END) FROM (
            SELECT (SELECT MIN(o.object_type) FROM dba_objects o WHERE o.owner = s.table_owner AND o.object_name = s.table_name
                       AND o.object_type NOT IN ('INDEX', 'TRIGGER', 'PACKAGE BODY', 'TYPE BODY')) AS target_type
              FROM dba_synonyms s WHERE s.owner = 'PUBLIC' AND s.db_link IS NULL)
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        output.WriteLine(Line($"[참고 PUBLIC 동의어 대상 확인 dba] {r.GetDecimal(0)}개 중 대상 없음 {r.GetDecimal(1)}개, {timer.Elapsed.TotalSeconds:0.00}초"));
    }

    private static async Task CreateObjectsAsync(OracleConnection raw, string u, string app, string appPassword)
    {
        string[] ddl =
        [
            $"CREATE USER {u} NO AUTHENTICATION QUOTA UNLIMITED ON USERS",
            $"GRANT CREATE TABLE, CREATE VIEW, CREATE MATERIALIZED VIEW, CREATE TRIGGER, CREATE TYPE, CREATE CLUSTER, CREATE SYNONYM, CREATE JOB TO {u}",
            $"CREATE USER {app} IDENTIFIED BY \"{appPassword}\"",
            $"GRANT CREATE SESSION TO {app}",
            $"CREATE TYPE {u}.T_PT AS OBJECT (x NUMBER)",
            $"CREATE TYPE {u}.T_VA AS VARRAY(5) OF NUMBER",
            $"CREATE TYPE {u}.T_NT AS TABLE OF NUMBER",
            $"CREATE TABLE {u}.OBJ_T OF {u}.T_PT",
            $"CREATE GLOBAL TEMPORARY TABLE {u}.GTT_S (a NUMBER) ON COMMIT PRESERVE ROWS",
            $"CREATE GLOBAL TEMPORARY TABLE {u}.GTT_T (a NUMBER) ON COMMIT DELETE ROWS",
            $"CREATE TABLE {u}.IOT_T (id NUMBER PRIMARY KEY) ORGANIZATION INDEX",
            $"CREATE TABLE {u}.PART_R (id NUMBER, d DATE) PARTITION BY RANGE (d) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION p0 VALUES LESS THAN (DATE '2026-01-01'))",
            $"CREATE INDEX {u}.PART_IX ON {u}.PART_R (id) LOCAL",
            $"CREATE CLUSTER {u}.C_DEPT (deptno NUMBER)",
            $"CREATE INDEX {u}.C_DEPT_IX ON CLUSTER {u}.C_DEPT",
            $"CREATE TABLE {u}.CL_T (deptno NUMBER, x NUMBER) CLUSTER {u}.C_DEPT (deptno)",
            $"CREATE TABLE {u}.HEAP_T (id NUMBER CONSTRAINT HEAP_PK PRIMARY KEY, a NUMBER, b VARCHAR2(10))",
            $"CREATE INDEX {u}.HEAP_IX ON {u}.HEAP_T (a)",
            $"CREATE INDEX {u}.HEAP_RX ON {u}.HEAP_T (a, b) REVERSE",
            $"CREATE INDEX {u}.HEAP_FX ON {u}.HEAP_T (UPPER(b))",
            $"ALTER INDEX {u}.HEAP_IX UNUSABLE",
            $"ALTER TABLE {u}.HEAP_T ADD CONSTRAINT HEAP_CK CHECK (a > 0) DISABLE",
            $"ALTER TABLE {u}.HEAP_T ADD CONSTRAINT HEAP_NV CHECK (a < 100) ENABLE NOVALIDATE",
            $"CREATE TABLE {u}.COL_T (id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, v NUMBER AS (id * 2) VIRTUAL, h NUMBER INVISIBLE, c CLOB)",
            $"CREATE VIEW {u}.V_HEAP AS SELECT id FROM {u}.HEAP_T",
            $"CREATE TRIGGER {u}.TRG_BR BEFORE INSERT ON {u}.HEAP_T FOR EACH ROW BEGIN NULL; END;",
            $"CREATE TRIGGER {u}.TRG_AS AFTER UPDATE ON {u}.HEAP_T BEGIN NULL; END;",
            $"ALTER TRIGGER {u}.TRG_AS DISABLE",
            $"CREATE TRIGGER {u}.TRG_IO INSTEAD OF INSERT ON {u}.V_HEAP FOR EACH ROW BEGIN NULL; END;",
            $"CREATE TRIGGER {u}.TRG_DDL AFTER CREATE ON {u}.SCHEMA BEGIN NULL; END;",
            $"CREATE SYNONYM {u}.SYN_OK FOR {u}.HEAP_T",
            $"CREATE SYNONYM {u}.SYN_BAD FOR {u}.NO_SUCH_TABLE",
            $"CREATE SYNONYM {u}.SYN_PKG FOR SYS.DBMS_OUTPUT",
            $"CREATE MATERIALIZED VIEW {u}.MV_C REFRESH COMPLETE ON DEMAND AS SELECT id, a FROM {u}.HEAP_T",
            $"INSERT INTO {u}.HEAP_T (id, a, b) VALUES (1, 1, 'x')",
            "COMMIT",
            $"BEGIN DBMS_SCHEDULER.CREATE_JOB(job_name => '{u}.JOB_OFF', job_type => 'PLSQL_BLOCK', job_action => 'BEGIN NULL; END;', enabled => FALSE); END;",
            $"BEGIN DBMS_SCHEDULER.CREATE_JOB(job_name => '{u}.JOB_ON', job_type => 'PLSQL_BLOCK', job_action => 'BEGIN NULL; END;', start_date => SYSTIMESTAMP + 1, repeat_interval => 'FREQ=DAILY', enabled => TRUE); END;",
        ];
        foreach (var sql in ddl)
        {
            try
            {
                await ExecAsync(raw, sql);
            }
            catch (OracleException ex)
            {
                throw new InvalidOperationException($"준비 문장 실패({ex.Number}): {sql.Replace(appPassword, "***", StringComparison.Ordinal)}", ex);
            }
        }
    }

    private static async Task<ISession> OpenAsync(ConnectionProfile profile, string password, List<ISession> sessions)
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var credential = new SecureCredential(password);
        var session = await manager.OpenAsync(profile, credential, SessionPurpose.Work, default);
        sessions.Add(session);
        return session;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
