using System.Runtime.ExceptionServices;
using Oranda.Core.Execution;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Transfer.Copy;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 복사 실행 방언(<see cref="TiberoCopyDialect"/>)이 만든 문장을 실제 서버로 보내 실측한다(T5 TB3b).
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만, ORT_ 이름만). 시험마다 시작할 때 남은 제 객체를 지우고 끝나면 지운다.
/// <list type="bullet">
/// <item>외래키: 끄기 → 고아 행 넣기 → VALIDATE 켜기 실패(TBR-7161) → NOVALIDATE 켜기 → 지우기 → ADD 실패 → NOVALIDATE 로 다시 붙이기 → 참조되는 부모 DROP 거부(TBR-7156).</item>
/// <item>트리거: 끄기·켜기가 <c>user_triggers.status</c> 에 반영되는가.</item>
/// <item>트랜잭션: 휴지통 켜기, 세이브포인트 되돌리기, 읽기 전용 블록.</item>
/// <item>시퀀스: 한 번에 N개 소비(<c>CONNECT BY LEVEL</c> + 바인드).</item>
/// <item>휴지통: 켠 뒤 DROP TABLE(PURGE 없음)이 휴지통에 들어가는가.</item>
/// </list>
/// 접속 값(계정·DB·호스트)은 단언 메시지·출력에 넣지 않는다: 참·거짓 단언만 쓰고, 예외는 형식 이름·오류 번호만 남긴다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCopyDialectIt(ITestOutputHelper output)
{
    private static readonly TiberoCopyDialect Dialect = TiberoCopyDialect.Instance;

    private static readonly string Parent = TiberoItSessions.ObjectName("CPD_P");
    private static readonly string Child = TiberoItSessions.ObjectName("CPD_C");
    private static readonly string Fk = TiberoItSessions.ObjectName("CPD_FK");
    private static readonly string TriggerTable = TiberoItSessions.ObjectName("CPD_TT");
    private static readonly string TriggerName = TiberoItSessions.ObjectName("CPD_TRG");
    private static readonly string TxTable = TiberoItSessions.ObjectName("CPD_TX");
    private static readonly string SnapshotTable = TiberoItSessions.ObjectName("CPD_RO");
    private static readonly string Sequence = TiberoItSessions.ObjectName("CPD_SEQ");
    private static readonly string BinTable = TiberoItSessions.ObjectName("CPD_BIN");
    private static readonly string PointTable = TiberoItSessions.ObjectName("CPD_ASOF");

    [TiberoWriteFact]
    public Task Foreign_key_disable_enable_novalidate_drop_readd_and_referenced_parent_drop() =>
        WriteAsync([Child, Parent], [], false, FkAsync);

    [TiberoWriteFact]
    public Task Trigger_disable_and_enable_change_the_catalog_status() =>
        WriteAsync([TriggerTable], [], false, TriggerAsync);

    [TiberoWriteFact]
    public Task Recycle_bin_on_savepoint_and_rollback_to_savepoint_are_accepted() =>
        WriteAsync([TxTable], [], false, TransactionAsync);

    [TiberoWriteFact]
    public Task Read_only_block_keeps_one_snapshot_until_rollback() =>
        WriteAsync([SnapshotTable], [], false, ReadOnlySnapshotAsync);

    [TiberoWriteFact]
    public Task Next_values_consumes_n_distinct_increasing_sequence_values() =>
        WriteAsync([], [Sequence], false, SequenceAsync);

    [TiberoWriteFact]
    public Task Drop_table_without_purge_goes_to_the_recycle_bin_when_it_is_on() =>
        WriteAsync([BinTable], [], true, RecycleBinAsync);

    /// <summary>
    /// PF2b 실측: 지금 시점 번호(TSN)를 읽는 방법과 <c>AS OF SCN</c> 이 그 시점의 행을 돌려주는지 본다. 방법마다 되는지·오류 번호를 출력한다
    /// (6.2.15 #187 에서 셋 다 됨 → PF2b2 가 Tibero 원본의 같은 시점 읽기 + 동시 복사를 켰다, <c>TiberoCopyDialect.ReadPoint</c>·<c>TiberoCopyReadPointIt</c>).
    /// </summary>
    [TiberoWriteFact]
    public Task Flashback_query_probe_reads_the_current_tsn_and_as_of_scn_returns_the_old_rows() =>
        WriteAsync([PointTable], [], false, FlashbackProbeAsync);

    private static async Task FkAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create parent", $"CREATE TABLE {Parent} (ID NUMBER PRIMARY KEY)");
        await ExecQuietAsync(session, "create child",
            $"CREATE TABLE {Child} (ID NUMBER PRIMARY KEY, PID NUMBER, CONSTRAINT {Fk} FOREIGN KEY (PID) REFERENCES {Parent} (ID))");
        await ExecQuietAsync(session, "insert parent", $"INSERT INTO {Parent} (ID) VALUES (1)");
        await ExecQuietAsync(session, "insert child", $"INSERT INTO {Child} (ID, PID) VALUES (1, 1)");
        await ExecQuietAsync(session, "commit setup", "COMMIT");

        var fk = new DisabledObject(DisabledKind.ForeignKey, owner, Child, Fk);

        await ExecQuietAsync(session, "DisableConstraint", Dialect.DisableConstraint(fk).Text);
        Assert.True(await CountAsync(session, FkStatusSql("DISABLED")) == 1, "DisableConstraint: constraint is not DISABLED in user_constraints");
        await ExecQuietAsync(session, "insert orphan child while disabled", $"INSERT INTO {Child} (ID, PID) VALUES (2, 99)");
        await ExecQuietAsync(session, "commit orphan", "COMMIT");

        var validate = await TryAsync(session, Dialect.EnableConstraint(fk, validate: true).Text);
        Assert.True(validate.Failed && Dialect.IsParentKeysNotFound(validate.Error), "EnableConstraint(validate) with an orphan row: expected TBR-7161, got " + validate.What);
        Assert.True(await CountAsync(session, FkStatusSql("DISABLED")) == 1, "EnableConstraint(validate) failed but the constraint is not left DISABLED");

        await ExecQuietAsync(session, "EnableConstraint(novalidate)", Dialect.EnableConstraint(fk, validate: false).Text);
        Assert.True(await CountAsync(session, FkStatusSql("ENABLED")) == 1, "EnableConstraint(novalidate): constraint is not ENABLED in user_constraints");

        await ExecQuietAsync(session, "DropConstraint", Dialect.DropConstraint(fk).Text);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Child}' AND constraint_name = '{Fk}'") == 0,
            "DropConstraint: constraint is still in user_constraints");

        var add = $"ALTER TABLE {Qualified(owner, Child)} ADD CONSTRAINT {Quote(Fk)} FOREIGN KEY ({Quote("PID")}) REFERENCES {Qualified(owner, Parent)} ({Quote("ID")})";
        var plainAdd = await TryAsync(session, add);
        Assert.True(plainAdd.Failed && Dialect.IsParentKeysNotFound(plainAdd.Error), "ADD CONSTRAINT FOREIGN KEY with an orphan row: expected TBR-7161, got " + plainAdd.What);

        var novalidateAdd = Dialect.AddForeignKeyWithoutValidation(add);
        var readd = await TryAsync(session, novalidateAdd);
        Assert.True(!readd.Failed, "AddForeignKeyWithoutValidation: ADD ... ENABLE NOVALIDATE failed with " + readd.What);
        Assert.True(await CountAsync(session, FkStatusSql("ENABLED")) == 1, "AddForeignKeyWithoutValidation: constraint is not ENABLED after the re-add");

        var drop = await TryAsync(session, Dialect.DropTable(owner, Parent).Text);
        Assert.True(drop.Failed && Dialect.IsReferencedByForeignKeys(drop.Error), "DropTable on a parent referenced by a foreign key: expected TBR-7156, got " + drop.What);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{Parent}' AND object_type = 'TABLE'") == 1,
            "DropTable was refused but the parent table is gone");
    }

    private static string FkStatusSql(string status) =>
        $"SELECT COUNT(*) FROM user_constraints WHERE table_name = '{Child}' AND constraint_name = '{Fk}' AND constraint_type = 'R' AND status = '{status}'";

    private static async Task TriggerAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create table", $"CREATE TABLE {TriggerTable} (ID NUMBER)");
        await ExecQuietAsync(session, "create trigger", $"CREATE TRIGGER {TriggerName} BEFORE INSERT ON {TriggerTable} FOR EACH ROW\nBEGIN\n  NULL;\nEND;");
        Assert.True(await CountAsync(session, TriggerStatusSql("ENABLED")) == 1, "setup: trigger is not ENABLED after create");

        await ExecQuietAsync(session, "DisableTrigger", Dialect.DisableTrigger(owner, TriggerName).Text);
        Assert.True(await CountAsync(session, TriggerStatusSql("DISABLED")) == 1, "DisableTrigger: trigger is not DISABLED in user_triggers");

        await ExecQuietAsync(session, "EnableTrigger", Dialect.EnableTrigger(owner, TriggerName).Text);
        Assert.True(await CountAsync(session, TriggerStatusSql("ENABLED")) == 1, "EnableTrigger: trigger is not ENABLED in user_triggers");
    }

    private static string TriggerStatusSql(string status) =>
        $"SELECT COUNT(*) FROM user_triggers WHERE trigger_name = '{TriggerName}' AND table_name = '{TriggerTable}' AND status = '{status}'";

    private async Task TransactionAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create table", $"CREATE TABLE {TxTable} (ID NUMBER)");
        await ExecQuietAsync(session, "RecycleBinOn", Dialect.RecycleBinOn().Text);

        await ExecQuietAsync(session, "insert before savepoint", $"INSERT INTO {TxTable} (ID) VALUES (1)");
        await ExecQuietAsync(session, "Savepoint", Dialect.Savepoint(CopyCommonStatements.BatchSavepoint).Text);
        await ExecQuietAsync(session, "insert after savepoint", $"INSERT INTO {TxTable} (ID) VALUES (2)");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {TxTable}") == 2, "both rows should be visible before the rollback to savepoint");

        await ExecQuietAsync(session, "RollbackToSavepoint", Dialect.RollbackToSavepoint(CopyCommonStatements.BatchSavepoint).Text);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {TxTable} WHERE ID = 2") == 0, "RollbackToSavepoint: the row inserted after the savepoint is still visible");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {TxTable} WHERE ID = 1") == 1, "RollbackToSavepoint: the row inserted before the savepoint is gone");
        await ExecQuietAsync(session, "ROLLBACK after savepoint", "ROLLBACK");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {TxTable}") == 0, "ROLLBACK: rows are still visible");

        output.WriteLine("transaction statements accepted: USE_RECYCLEBIN, SAVEPOINT, ROLLBACK TO SAVEPOINT");
    }

    /// <summary>
    /// 읽기 전용 블록의 목적 = 같은 시점 읽기(3.2): 블록 뒤 다른 세션이 넣고 커밋한 행이 ROLLBACK 전까지 보이지 않고, ROLLBACK 뒤에는 보인다.
    /// (Tibero 의 READ ONLY 는 쓰기를 막지 않으므로 쓰기 거부는 보지 않는다.)
    /// </summary>
    private static async Task ReadOnlySnapshotAsync(TiberoItSessions it, TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create table", $"CREATE TABLE {SnapshotTable} (ID NUMBER)");
        await ExecQuietAsync(session, "ROLLBACK before read-only", "ROLLBACK");
        await ExecQuietAsync(session, "SetTransactionReadOnly", Dialect.SetTransactionReadOnly().Text);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {SnapshotTable}") == 0, "read-only: the new table should be empty");

        var other = await QuietAsync("open second session", () => it.OpenAsync());
        await ExecQuietAsync(other, "second session insert", $"INSERT INTO {SnapshotTable} (ID) VALUES (1)");
        await ExecQuietAsync(other, "second session commit", "COMMIT");

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {SnapshotTable}") == 0,
            "read-only block: a row committed by another session became visible inside the read-only transaction (no snapshot)");
        await ExecQuietAsync(session, "ROLLBACK after read-only", "ROLLBACK");
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {SnapshotTable}") == 1, "after ROLLBACK the committed row should be visible");
    }

    private async Task SequenceAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create sequence", $"CREATE SEQUENCE {Sequence} START WITH 1 INCREMENT BY 1 NOCACHE");

        var statement = Dialect.NextValues(owner, Sequence);
        var table = await QuietAsync("NextValues", () => TiberoSessionQuery.RunAsync(
            session, statement.Text, cmd => TiberoSessionQuery.Bind(cmd, CopyCommonStatements.NextValuesBind, 5m)));
        var values = table.Rows.Select(r => TiberoSessionQuery.Long(r[0]) ?? long.MinValue).ToList();
        Assert.True(values.Count == 5, "NextValues(N = 5) returned " + values.Count + " rows, expected 5");
        Assert.True(values.Distinct().Count() == 5, "NextValues returned duplicate values: " + string.Join(",", values));
        Assert.True(values.Zip(values.Skip(1), (a, b) => b > a).All(x => x), "NextValues values are not strictly increasing: " + string.Join(",", values));
        output.WriteLine("NextValues(N = 5) returned " + string.Join(",", values));
    }

    private async Task RecycleBinAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create table", $"CREATE TABLE {BinTable} (ID NUMBER)");
        await ExecQuietAsync(session, "RecycleBinOn", Dialect.RecycleBinOn().Text);

        var drop = Dialect.DropTable(owner, BinTable);
        Assert.True(!drop.Text.Contains("PURGE", StringComparison.Ordinal), "DropTable text must not contain PURGE");
        await ExecQuietAsync(session, "DropTable", drop.Text);

        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{BinTable}' AND object_type = 'TABLE'") == 0,
            "DropTable: the table still exists under its name");
        var inBin = await CountAsync(session, $"SELECT COUNT(*) FROM user_recyclebin WHERE original_name = '{BinTable}'");
        Assert.True(inBin == 1, "DropTable with the recycle bin on: expected 1 recycle bin row for the table, found " + inBin);
        output.WriteLine("recycle bin row found for the dropped table (user_recyclebin.original_name)");
    }

    private async Task FlashbackProbeAsync(TiberoSession session, string owner)
    {
        await ExecQuietAsync(session, "create table", $"CREATE TABLE {PointTable} (ID NUMBER)");
        await ExecQuietAsync(session, "insert first row", $"INSERT INTO {PointTable} (ID) VALUES (1)");
        await ExecQuietAsync(session, "commit first row", "COMMIT");
        await Task.Delay(TimeSpan.FromSeconds(8)); // 표를 만든 직후의 시점으로는 읽지 못할 수 있다(Oracle ORA-01466 과 같은 규칙인지 모름)

        long? point = null;
        foreach (var (sql, method) in new[]
                 {
                     ("SELECT CURRENT_TSN FROM V$DATABASE", "V$DATABASE.CURRENT_TSN"),
                     ("SELECT TIMESTAMP_TO_TSN(SYSTIMESTAMP) FROM DUAL", "TIMESTAMP_TO_TSN(SYSTIMESTAMP)"),
                     ("SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL", "DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER"),
                 })
        {
            try
            {
                var value = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql));
                output.WriteLine($"{method}: {(value is { } v ? "ok " + v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no value")}");
                point ??= value;
            }
#pragma warning disable CA1031 // 실측: 방법마다 되는지만 남긴다(형식 이름·오류 번호만).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"{method}: failed {Describe(ex)}");
            }
        }

        Assert.True(point is > 0, "no way to read the current TSN worked (see output)");
        await ExecQuietAsync(session, "insert late row", $"INSERT INTO {PointTable} (ID) VALUES (2)");
        await ExecQuietAsync(session, "commit late row", "COMMIT");

        var text = point!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var clause in new[] { "AS OF TSN " + text, "AS OF SCN " + text })
        {
            var label = string.Join(' ', clause.Split(' ').Take(3));
            try
            {
                var seen = TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, $"SELECT COUNT(*) FROM {Qualified(owner, PointTable)} {clause}"));
                output.WriteLine($"{label}: ok, rows {seen?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}");
            }
#pragma warning disable CA1031 // 실측: 글마다 되는지만 남긴다(형식 이름·오류 번호만).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"{label}: failed {Describe(ex)}");
            }
        }

        // 복사가 쓸 글(Oracle 과 같은 AS OF SCN): 늦게 넣은 행이 보이지 않아야 한다.
        var asOf = await CountAsync(session, $"SELECT COUNT(*) FROM {Qualified(owner, PointTable)} AS OF SCN {text}");
        Assert.True(asOf == 1, "AS OF SCN <current TSN before the late row> should see 1 row, saw " + asOf);
        Assert.True(await CountAsync(session, $"SELECT COUNT(*) FROM {Qualified(owner, PointTable)}") == 2, "the late row should be visible now");
    }

    /// <summary>쓰기 시험 틀: 남은 제 객체를 지우고 본문을 돌린 뒤 다시 지운다. 본문 실패는 정리 뒤 그대로 다시 던진다.</summary>
    private static Task WriteAsync(string[] tables, string[] sequences, bool purgeBin, Func<TiberoSession, string, Task> body) =>
        WriteAsync(tables, sequences, purgeBin, (_, session, owner) => body(session, owner));

    /// <inheritdoc cref="WriteAsync(string[], string[], bool, Func{TiberoSession, string, Task})"/>
    private static async Task WriteAsync(string[] tables, string[] sequences, bool purgeBin, Func<TiberoItSessions, TiberoSession, string, Task> body)
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        await CleanupAsync(session, tables, sequences, purgeBin);

        Exception? failure = null;
        try
        {
            await body(it, session, it.Env.User.ToUpperInvariant());
        }
#pragma warning disable CA1031 // 본문 실패를 잡아 두고 정리한 뒤 그대로 다시 던진다(정리가 본문 실패를 가리지 않게).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }

        await CleanupAsync(session, tables, sequences, purgeBin);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>미커밋 변경을 되돌리고 표·시퀀스(·휴지통)를 지운다. 오류는 무시한다(없는 객체 등).</summary>
    private static async Task CleanupAsync(TiberoSession session, string[] tables, string[] sequences, bool purgeBin)
    {
        await IgnoreAsync(session, "ROLLBACK");
        foreach (var table in tables)
        {
            await IgnoreAsync(session, $"DROP TABLE \"{table}\" CASCADE CONSTRAINTS PURGE");
        }

        foreach (var sequence in sequences)
        {
            await IgnoreAsync(session, $"DROP SEQUENCE \"{sequence}\"");
        }

        if (purgeBin)
        {
            await PurgeOwnBinItemsAsync(session, tables);
        }
    }

    /// <summary>
    /// 휴지통에서 이 시험이 지운 ORT_ 표의 항목만 비운다(BIN 이름으로 <c>PURGE TABLE</c>, 3.7 실측). <c>PURGE RECYCLEBIN</c> 은 쓰지 않는다:
    /// 시험 계정 휴지통의 다른 항목까지 지운다(TM0b 규칙). 오류는 무시한다.
    /// </summary>
    private static async Task PurgeOwnBinItemsAsync(TiberoSession session, IEnumerable<string> tables)
    {
        foreach (var table in tables)
        {
            TiberoSessionQuery.Table items;
            try
            {
                items = await TiberoSessionQuery.RunAsync(session, "SELECT object_name FROM user_recyclebin WHERE original_name = :t",
                    c => TiberoSessionQuery.Bind(c, "t", table));
            }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            foreach (var row in items.Rows)
            {
                if (TiberoSessionQuery.Str(row[0]) is { Length: > 0 } binName)
                {
                    await IgnoreAsync(session, "PURGE TABLE " + Quote(binName));
                }
            }
        }
    }

    private static async Task IgnoreAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 오류는 무시한다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string Quote(string name) => SqlIdentifier.QuoteAlways(name);

    private static string Qualified(string owner, string name) => CopyCommonStatements.Qualified(owner, name);

    private static Task<long> CountAsync(TiberoSession session, string sql) =>
        QuietAsync("count query", async () => TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0);

    /// <summary>문장을 보내 실패 여부·서버 오류·(형식 이름·오류 번호만 담은) 설명을 돌려준다.</summary>
    private static async Task<(bool Failed, DbError? Error, string What)> TryAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return (false, null, "success");
        }
#pragma warning disable CA1031 // 실패를 오류 값으로 바꿔 돌려준다(접속 값 없는 형식 이름·오류 번호만).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (true, TiberoErrorClassifier.ToDbError(ex, sql), Describe(ex));
        }
    }

    /// <summary>예외 메시지에는 <c>소유자.이름</c>(접속 계정)이 실릴 수 있다. 형식 이름과 서버 오류 번호만 담아 다시 던진다.</summary>
    private static async Task<T> QuietAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
#pragma warning disable CA1031 // 시험 실패로 바꿔 던진다(메시지에서 접속 계정을 뺀다).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new Xunit.Sdk.XunitException(what + " failed: " + Describe(ex));
        }
    }

    private static string Describe(Exception ex)
    {
        var code = TiberoErrorClassifier.ToDbError(ex, null)?.Code;
        return ex.GetType().Name + (code is null ? string.Empty : " " + code);
    }

    private static async Task ExecQuietAsync(TiberoSession session, string what, string sql) =>
        await QuietAsync(what, async () => { await ExecAsync(session, sql); return true; });

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
