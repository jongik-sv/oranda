using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Support;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 마감 뒤 고친 것(te-followup)을 실제 Tibero 서버로 본다(레인 te-w30-verify).
/// <list type="bullet">
/// <item>열을 더하고 지운 뒤 같은 세션에서 같은 질의를 실행 엔진으로 다시 읽기(Oracle ODP 는 스키마 표 캐시로 실패했다).</item>
/// <item>파티션 경계 글(사전 BOUND 그대로)과 <see cref="PartitionBounds.ToPlain"/>, INTERVAL 켜기·끄기·자동 파티션 이름.</item>
/// <item>객체 상세 「저장」 구역(<see cref="TableStorageSection"/>)의 값.</item>
/// </list>
/// 환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>. 공유 계정이라 <c>ORT_TE_</c> 객체만 만들고 끝에 지운다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoTableEditFollowupIt(ITestOutputHelper output)
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private static string T(string suffix) => TiberoItSessions.ObjectName("TE_" + suffix);

    /// <summary>
    /// 열 더하기(가상·보통)·지우기 뒤 같은 세션·같은 문장 다시 읽기: 실행이 성공하고 새 열까지 보이며, 스키마 표를 그대로 읽어 ROWID 열이 ROWID 로 남는다
    /// (스키마 표가 옛것이라 <see cref="Results.TiberoColumns.SchemaOrNull"/> 가 건너뛰면 ROWID 표시가 사라진다).
    /// </summary>
    [TiberoWriteFact]
    public async Task Same_session_reads_the_table_again_after_columns_are_added_and_dropped()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("RF");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(20), OLD_C VARCHAR2(5))");
            await p.MustAsync($"INSERT INTO {t} VALUES (1, 'a', 'x')");
            await p.MustAsync($"INSERT INTO {t} VALUES (2, 'b', 'y')");
            await p.MustAsync("COMMIT");
            var service = new TiberoExecutionService(new AllowAll(), ExecutionFakes.NoQuestions(), it.Events, null, dbmsOutput: false);
            var query = $"SELECT t.ROWID, t.* FROM {t} t ORDER BY t.ID";

            Assert.Equal(["ROWID", "ID", "NAME", "OLD_C"], await ReadAsync(service, session, query, 2));

            await RunDdlAsync(service, session, $"ALTER TABLE {t} ADD (NOTE VARCHAR2(30) DEFAULT 'n')");
            Assert.Equal(["ROWID", "ID", "NAME", "OLD_C", "NOTE"], await ReadAsync(service, session, query, 2));

            await RunDdlAsync(service, session, $"ALTER TABLE {t} DROP COLUMN OLD_C");
            Assert.Equal(["ROWID", "ID", "NAME", "NOTE"], await ReadAsync(service, session, query, 2));

            // 열 수가 같고 형식만 다른 경우(지우고 같은 이름으로 다시 더하기).
            await RunDdlAsync(service, session, $"ALTER TABLE {t} DROP COLUMN NOTE");
            await RunDdlAsync(service, session, $"ALTER TABLE {t} ADD (NOTE DATE)");
            Assert.Equal(["ROWID", "ID", "NAME", "NOTE"], await ReadAsync(service, session, query, 2, lastKind: ColumnDataKind.Date));

            await RunDdlAsync(service, session, $"ALTER TABLE {t} ADD (V1 NUMBER GENERATED ALWAYS AS (ID * 2) VIRTUAL)");
            Assert.Equal(["ROWID", "ID", "NAME", "NOTE", "V1"], await ReadAsync(service, session, query, 2));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>
    /// Tibero 서버 결함 실측(te-w30-verify): 가상 열이 있는 테이블에서 열을 지우면 그 값이 남아, 뒤에 더한 열(이름·형식이 달라도)이 지운 열의 값을 보인다.
    /// 같은 이름을 DATE 로 다시 더하면 옛 글 바이트를 DATE 로 풀다 드라이버가 ODP-590750(corrupted DATE)을 던진다 — 세션·문장 캐시가 아니라 저장된 값이다(새 세션도 같다).
    /// 가상 열이 없으면 생기지 않는다(<see cref="Same_session_reads_the_table_again_after_columns_are_added_and_dropped"/>).
    /// 서버가 고쳐지면 이 시험이 깨진다 — 그때 테이블 편집기 경고를 거둔다.
    /// </summary>
    [TiberoWriteFact]
    public async Task With_a_virtual_column_a_dropped_columns_values_reappear_in_the_next_added_column()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (_, p) = await OpenAsync(it);
        var t = T("RFV");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(20), V1 AS (ID * 2) VIRTUAL)");
            await p.MustAsync($"INSERT INTO {t} (ID, NAME) VALUES (1, 'a')");
            await p.MustAsync($"ALTER TABLE {t} ADD (NOTE VARCHAR2(30))");
            await p.MustAsync($"UPDATE {t} SET NOTE = 'm'");
            await p.MustAsync("COMMIT");
            await p.ExpectAsync("rfv drop", "OK", $"ALTER TABLE {t} DROP COLUMN NOTE");
            await p.ExpectAsync("rfv add other name", "OK", $"ALTER TABLE {t} ADD (NOTE2 VARCHAR2(30))");
            var rows = await p.DumpAsync("rfv", $"SELECT NOTE2, DUMP(NOTE2) FROM {t}");
            Assert.Equal("m", Assert.Single(rows)[0]); // 새 열인데 NULL 이 아니라 지운 열의 값

            await p.MustAsync($"ALTER TABLE {t} DROP COLUMN NOTE2");
            await p.MustAsync($"ALTER TABLE {t} ADD (NOTE DATE)");
            var other = await it.OpenAsync(SessionPurpose.Work); // 하니스가 닫는다
            var fresh = new TableEditProbe(other, output, p.Owner, it.Env.DbName);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => fresh.RowsAsync($"SELECT NOTE FROM {t}"));
            p.Log("rfv date (new session)", ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
            Assert.Contains("590750", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>
    /// 3b: 공급자가 돌려주는 <see cref="PartitionInfo.Bound"/> 글(DATE 키 RANGE 의 <c>DATE '…'</c>·<c>TO_DATE(…)</c>, NUMBER 키, VARCHAR2 키 LIST,
    /// INTERVAL 로 서버가 만든 파티션)을 출력에 남기고 <see cref="PartitionBounds.ToPlain"/> 이 보통 값으로 읽는지 본다.
    /// </summary>
    [TiberoWriteFact]
    public async Task Partition_bounds_read_back_as_plain_values()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var dateLit = T("PB_D1");
        var dateFn = T("PB_D2");
        var num = T("PB_N");
        var list = T("PB_L");
        var interval = T("PB_I");
        string[] tables = [dateLit, dateFn, num, list, interval];
        foreach (var x in tables)
        {
            await p.DropTableAsync(x);
        }

        try
        {
            await p.MustAsync($"CREATE TABLE {dateLit} (ID NUMBER, D DATE) PARTITION BY RANGE (D) (PARTITION P1 VALUES LESS THAN (DATE '2026-11-01'), PARTITION P2 VALUES LESS THAN (DATE '2026-12-01'), PARTITION PM VALUES LESS THAN (MAXVALUE))");
            await p.MustAsync($"CREATE TABLE {dateFn} (ID NUMBER, D DATE) PARTITION BY RANGE (D) (PARTITION P1 VALUES LESS THAN (TO_DATE('2026-11-01', 'YYYY-MM-DD')), PARTITION P2 VALUES LESS THAN (TO_DATE('2026-12-01 13:30:00', 'YYYY-MM-DD HH24:MI:SS')))");
            await p.MustAsync($"CREATE TABLE {num} (ID NUMBER) PARTITION BY RANGE (ID) (PARTITION P1 VALUES LESS THAN (1000), PARTITION P2 VALUES LESS THAN (2000.5))");
            await p.MustAsync($"CREATE TABLE {list} (ID NUMBER, C VARCHAR2(5)) PARTITION BY LIST (C) (PARTITION PAB VALUES ('A', 'B'), PARTITION PC VALUES ('C'), PARTITION PN VALUES (NULL), PARTITION PX VALUES (DEFAULT))");
            await p.MustAsync($"CREATE TABLE {interval} (ID NUMBER, D DATE) PARTITION BY RANGE (D) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P1 VALUES LESS THAN (DATE '2026-11-01'))");
            await p.MustAsync($"INSERT INTO {interval} VALUES (1, DATE '2027-01-15')");
            await p.MustAsync("COMMIT");

            var d1 = await BoundsAsync(session, p, dateLit);
            Assert.Equal(["2026-11-01", "2026-12-01", "MAXVALUE"], d1.Select(b => PartitionBounds.ToPlain(b.Bound)));
            Assert.Equal(new DateTime(2026, 12, 1), PartitionBounds.AsDate(d1[1].Bound));

            var d2 = await BoundsAsync(session, p, dateFn);
            Assert.Equal(["2026-11-01", "2026-12-01 13:30:00"], d2.Select(b => PartitionBounds.ToPlain(b.Bound)));

            var n = await BoundsAsync(session, p, num);
            Assert.Equal(["1000", "2000.5"], n.Select(b => PartitionBounds.ToPlain(b.Bound)));
            Assert.Equal(1000m, PartitionBounds.AsNumber(n[0].Bound));

            var l = await BoundsAsync(session, p, list);
            Assert.Equal(["A, B", "C", "NULL", "DEFAULT"], l.Select(b => PartitionBounds.ToPlain(b.Bound)));

            var i = await BoundsAsync(session, p, interval);
            Assert.Equal(2, i.Count);
            Assert.Equal(["2026-11-01", "2027-02-01"], i.Select(b => PartitionBounds.ToPlain(b.Bound)));
            var info = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, interval, "TABLE"), CancellationToken.None);
            p.Log("pb interval", "Interval = " + TableEditProbe.Show(info.Partitioning!.Interval));
            Assert.Equal(IntervalUnit.Month, PartitionBounds.UnitOf(info.Partitioning.Interval));
        }
        finally
        {
            foreach (var x in tables)
            {
                await p.DropTableAsync(x);
            }
        }
    }

    /// <summary>
    /// 3c: <c>SET INTERVAL (NUMTOYMINTERVAL(1,'MONTH'))</c>·<c>SET INTERVAL ()</c> 이 되고, 켠 뒤 범위 밖 행을 넣으면 서버가 파티션을 만든다(이름 꼴을 남긴다).
    /// 방언은 SetInterval 을 막지 않는다(<see cref="TiberoTableEditDialect.UnsupportedPartitionReasonKey"/> = null).
    /// </summary>
    [TiberoWriteFact]
    public async Task Set_interval_turns_automatic_partitions_on_and_off()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var t = T("PSI");
        await p.DropTableAsync(t);
        try
        {
            Assert.Null(new TiberoTableEditDialect().UnsupportedPartitionReasonKey(PartitionOperation.SetInterval, PartitionMethod.Range, null));
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER, D DATE) PARTITION BY RANGE (D) (PARTITION P_2026_10 VALUES LESS THAN (DATE '2026-11-01'))");
            await p.ExpectAsync("psi out of range (no interval)", "TBR-10018", $"INSERT INTO {t} VALUES (1, DATE '2026-12-15')");

            await p.ExpectAsync("psi set month", "OK", $"ALTER TABLE {t} SET INTERVAL (NUMTOYMINTERVAL(1,'MONTH'))");
            var on = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, t, "TABLE"), CancellationToken.None);
            p.Log("psi", "Interval after SET = " + TableEditProbe.Show(on.Partitioning!.Interval));
            Assert.Equal(IntervalUnit.Month, PartitionBounds.UnitOf(on.Partitioning.Interval));

            await p.ExpectAsync("psi insert", "OK", $"INSERT INTO {t} VALUES (1, DATE '2026-12-15')");
            await p.MustAsync("COMMIT");
            var made = await BoundsAsync(session, p, t);
            Assert.Equal(2, made.Count);
            Assert.Equal("2027-01-01", PartitionBounds.ToPlain(made[1].Bound));
            p.Log("psi", "server partition name shape = " + Shape(made[1].Name, p.Owner) + " isServerName=" + new TiberoTableEditDialect().IsServerName(made[1].Name));

            await p.ExpectAsync("psi set off", "OK", $"ALTER TABLE {t} SET INTERVAL ()");
            var off = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, t, "TABLE"), CancellationToken.None);
            Assert.Null(off.Partitioning!.Interval);
            Assert.Equal(IntervalUnit.Off, PartitionBounds.UnitOf(off.Partitioning.Interval));
            await p.ExpectAsync("psi out of range (off again)", "TBR-10018", $"INSERT INTO {t} VALUES (2, DATE '2027-03-15')");
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }

    /// <summary>3d: 객체 상세 「저장」 구역 — LOB 열 있는 테이블, 압축·PCTFREE 를 바꾼 테이블의 행 값.</summary>
    [TiberoWriteFact]
    public async Task Detail_storage_section_shows_table_and_lob_storage()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var (session, p) = await OpenAsync(it);
        var lob = T("ST_L");
        var tuned = T("ST_C");
        await p.DropTableAsync(lob);
        await p.DropTableAsync(tuned);
        try
        {
            await p.MustAsync($"CREATE TABLE {lob} (ID NUMBER, DOC CLOB, BIN BLOB) LOB (DOC) STORE AS (ENABLE STORAGE IN ROW) LOB (BIN) STORE AS (DISABLE STORAGE IN ROW)");
            await p.MustAsync($"CREATE TABLE {tuned} (ID NUMBER, V VARCHAR2(10)) PCTFREE 25 INITRANS 4 COMPRESS NOLOGGING");

            var ts = await p.DefaultTablespaceAsync();
            var lobRows = await StorageRowsAsync(session, p, lob);
            Assert.Equal(["테이블스페이스", "PCTFREE", "INITRANS", "압축", "LOGGING", "병렬", "LOB BIN", "LOB DOC"], lobRows.Keys); // LOB 줄 = 사전 순서(열 이름순)
            Assert.True(TiberoItSessions.Same(ts, lobRows["테이블스페이스"]), "tablespace row is not the default tablespace");
            Assert.Contains("행 안에", lobRows["LOB DOC"], StringComparison.Ordinal);
            Assert.Contains("행 밖에", lobRows["LOB BIN"], StringComparison.Ordinal);
            Assert.DoesNotContain("SECUREFILE", lobRows["LOB DOC"], StringComparison.Ordinal); // Tibero 사전에 방식이 없다

            var tunedRows = await StorageRowsAsync(session, p, tuned);
            Assert.Equal("25", tunedRows["PCTFREE"]);
            Assert.Equal("4", tunedRows["INITRANS"]);
            Assert.Equal("아니오", tunedRows["LOGGING"]);
            Assert.NotEqual("없음", tunedRows["압축"]);
            Assert.DoesNotContain(tunedRows.Keys, k => k.StartsWith("LOB ", StringComparison.Ordinal));

            await p.ExpectAsync("st alter", "OK", $"ALTER TABLE {tuned} PCTFREE 10");
            Assert.Equal("10", (await StorageRowsAsync(session, p, tuned))["PCTFREE"]);
        }
        finally
        {
            await p.DropTableAsync(lob);
            await p.DropTableAsync(tuned);
        }
    }

    /// <summary>파티션 이름을 꼴로(계정 이름 → &lt;owner&gt;, 숫자 → n).</summary>
    private static string Shape(string name, string owner) =>
        System.Text.RegularExpressions.Regex.Replace(name.Replace(owner, "<owner>", StringComparison.Ordinal), "[0-9]+", "n");

    private async Task<Dictionary<string, string>> StorageRowsAsync(TiberoSession session, TableEditProbe p, string table)
    {
        var detail = await TableEditProbe.NewProvider().GetObjectDetailAsync(session, new DbObjectRef(p.Owner, table, "TABLE"), CancellationToken.None);
        var section = Assert.Single(detail.Sections, s => s.Title == TableStorageSection.Title);
        Assert.Equal(["항목", "값"], section.Columns);
        var rows = section.Rows.ToDictionary(r => (string)r[0]!, r => (string)r[1]!, StringComparer.Ordinal);
        foreach (var (k, v) in rows)
        {
            p.Log("storage " + table[T(string.Empty).Length..], $"{k} = {v}");
        }

        output.WriteLine($"[storage] sections = {string.Join(", ", detail.Sections.Select(s => s.Title))}");
        return rows;
    }

    private static async Task<List<PartitionInfo>> BoundsAsync(TiberoSession session, TableEditProbe p, string table)
    {
        var info = await TableEditProbe.NewProvider().GetTableAsync(session, new DbObjectRef(p.Owner, table, "TABLE"), CancellationToken.None);
        Assert.True(info.IsPartitioned, table + " is not read as partitioned");
        var parts = info.Partitioning!.Partitions.OrderBy(x => x.Position).ToList();
        foreach (var x in parts)
        {
            p.Log("bound " + table[T(string.Empty).Length..], $"{Shape(x.Name, p.Owner)} #{x.Position} Bound={TableEditProbe.Show(x.Bound)} → plain={TableEditProbe.Show(PartitionBounds.ToPlain(x.Bound))}");
        }

        return parts;
    }

    private static async Task RunDdlAsync(TiberoExecutionService service, TiberoSession session, string sql)
    {
        var r = await service.ExecuteAsync(session, new SqlStatement(sql, default, StatementKind.Ddl) { Verb = "ALTER" }, NoBinds, null, CancellationToken.None);
        Assert.True(r.Status == ExecutionStatus.Succeeded, $"DDL failed: {r.Status} {r.Error?.Code}");
    }

    /// <summary>실행 엔진으로 질의를 돌려 첫 쪽을 읽고 열 이름을 돌려준다. ROWID 열은 ROWID 분류여야 한다(스키마 표를 읽었다는 뜻).</summary>
    private async Task<List<string>> ReadAsync(TiberoExecutionService service, TiberoSession session, string sql, int expectedRows, ColumnDataKind? lastKind = null)
    {
        var r = await service.ExecuteAsync(session, new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT" }, NoBinds, null, CancellationToken.None);
        Assert.True(r.Status == ExecutionStatus.Succeeded, $"query failed: {r.Status} {r.Error?.Code} {r.Error?.Message}");
        var rs = Assert.IsAssignableFrom<IResultSet>(r.ResultSet);
        await using (rs)
        {
            var names = rs.Columns.Select(c => c.Name.ToUpperInvariant()).ToList();
            output.WriteLine("[rf] columns = " + string.Join(", ", rs.Columns.Select(c => $"{c.Name}:{c.DbTypeName}:{c.Kind}")));
            Assert.Equal(ColumnDataKind.RowId, rs.Columns[0].Kind);
            if (lastKind is { } k)
            {
                Assert.Equal(k, rs.Columns[^1].Kind);
            }

            var page = await rs.GetPageAsync(0, CancellationToken.None);
            Assert.Equal(expectedRows, page.RowCount);
            Assert.Equal(names.Count, page.Columns.Count);
            return names;
        }
    }

    private async Task<(TiberoSession Session, TableEditProbe Probe)> OpenAsync(TiberoItSessions it)
    {
        var session = await it.OpenAsync(SessionPurpose.Work);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        var probe = new TableEditProbe(session, output, owner!.ToUpperInvariant(), it.Env.DbName);
        await probe.DefaultTablespaceAsync();
        return (session, probe);
    }
}
