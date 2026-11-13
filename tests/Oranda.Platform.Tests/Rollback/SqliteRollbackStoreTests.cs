using Microsoft.Data.Sqlite;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Security;
using Oranda.Platform.Credentials;
using Oranda.Platform.Rollback;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Rollback;

// 소유: RB-21. 되돌리기 지점 저장소와 마이그레이션 0005(설계 12장).
public sealed class SqliteRollbackStoreTests : IDisposable
{
    private const string Secret = "Hunter2xyz";
    private const string Db = "FREE/FREEPDB1/APP";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    public SqliteRollbackStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string DbFile => Path.Combine(_dir, "oranda.db");

    private string RollbackDir => Path.Combine(_dir, "rollback");

    private SqliteRollbackStore Store(IRollbackVault? vault = null)
        => SqliteRollbackStore.ForFile(DbFile, RollbackDir, vault ?? new RollbackVault(_credentials), new FakeRedactor(), _time,
            processStartedAt: _time.GetUtcNow().AddHours(1));

    private static RollbackGroup Group(DateTimeOffset started, RollbackScriptState state = RollbackScriptState.Confirmed, string table = "EMP")
        => new()
        {
            ProfileId = Guid.Parse("0b6f3c1e-1111-4222-8333-444455556666"),
            ProfileName = "prod-example",
            DbKey = Db,
            ServerName = "prod-example/FREEPDB1",
            Environment = ConnectionEnvironment.Production,
            Dbms = Dbms.Oracle,
            StartedAt = started,
            EndedAt = started.AddMinutes(1),
            BasePoint = new ChangePoint(48213907, "SCN", started, "GET_SYSTEM_CHANGE_NUMBER"),
            CommitPoint = new ChangePoint(48213999, "SCN", started.AddMinutes(1), "GET_SYSTEM_CHANGE_NUMBER"),
            TransactionId = "7.12.3456",
            EndKind = TransactionEndKind.Commit,
            EndSource = TransactionEndSource.Statement,
            State = state,
            StepCount = 2,
            RowsTotal = 2,
            Tables = [new DbObjectRef("APP", table, "TABLE")],
            Statements =
            [
                new RollbackStatementInfo(1, started, DmlVerb.Update, new DbObjectRef("APP", table, "TABLE"),
                    "UPDATE " + table + " SET pwd = '" + Secret + "'", 2, new ChangePoint(48213907, "SCN", started, "x"),
                    RollbackScriptState.Open, null, 2, false),
            ],
        };

    private static RollbackScriptDocument Document() => new(1, 0,
    [
        new RollbackScriptStatement(1, new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), "UPDATE EMP SET sal = :p WHERE id = 7",
            [new RollbackBind("p", BindDataType.Number, "1000")],
            [new RollbackStep("UPDATE APP.EMP SET SAL = 900 WHERE ID = 7") { Note = "행 1" }],
            [new RollbackProbe("SELECT COUNT(*) FROM APP.EMP WHERE ID = 7 AND SAL = 1000", "SELECT COUNT(*) FROM APP.EMP WHERE ID = 7 AND SAL = 900")],
            false, null, 1),
    ]);

    [Fact]
    public async Task Migration_0005_creates_rollback_tables()
    {
        using (var store = Store())
        {
            await store.SearchAsync(new RollbackQuery(), default);
        }

        SqliteConnection.ClearAllPools();
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        Assert.True(OrandaDatabase.LatestVersion >= 5);
        Assert.Equal(OrandaDatabase.LatestVersion, await OrandaDatabase.GetVersionAsync(connection, default));
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('rollback_group', 'rollback_statement')";
        Assert.Equal(2L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Save_writes_rows_and_encrypted_file_that_round_trip()
    {
        using var store = Store();
        var started = _time.GetUtcNow();

        var id = await store.SaveAsync(Group(started), Document(), default);

        Assert.True(id > 0);
        var file = Path.Combine(RollbackDir, "2026-11", id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".orb");
        Assert.True(File.Exists(file));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Equal("ORB1"u8.ToArray(), bytes[..4]);
        Assert.DoesNotContain("UPDATE EMP", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var group = await store.GetAsync(id, default);
        Assert.NotNull(group);
        Assert.True(group.HasScriptFile);
        Assert.Equal(48213907, group.BasePoint?.Number);
        Assert.Equal(48213999, group.CommitPoint?.Number);
        Assert.Equal("SCN", group.CommitPoint?.Name);
        Assert.Equal(TransactionEndKind.Commit, group.EndKind);
        Assert.Equal(new DbObjectRef("APP", "EMP", "TABLE"), Assert.Single(group.Tables));
        var statement = Assert.Single(group.Statements);
        Assert.Equal("UPDATE EMP SET pwd = '***'", statement.Summary);
        Assert.Equal(DmlVerb.Update, statement.Verb);

        var document = await store.LoadDocumentAsync(id, default);
        Assert.NotNull(document);
        Assert.Equal(id, document.GroupId);
        var s = Assert.Single(document.Statements);
        Assert.Equal("UPDATE EMP SET sal = :p WHERE id = 7", s.Text);
        Assert.Equal(new RollbackBind("p", BindDataType.Number, "1000"), Assert.Single(s.Binds));
        Assert.Equal("행 1", Assert.Single(s.Steps).Note);
    }

    [Fact]
    public async Task Overwrite_without_document_keeps_file_and_updates_state_and_reason()
    {
        using var store = Store();
        var id = await store.SaveAsync(Group(_time.GetUtcNow(), RollbackScriptState.Open), Document(), default);
        var opened = (await store.GetAsync(id, default))!;

        await store.SaveAsync(opened with { State = RollbackScriptState.NotCreated, Reason = NotCreatedReason.OverLimit, StateReason = "행 " + Secret }, null, default);

        var saved = (await store.GetAsync(id, default))!;
        Assert.Equal(RollbackScriptState.NotCreated, saved.State);
        Assert.Equal(NotCreatedReason.OverLimit, saved.Reason);
        Assert.Equal("행 ***", saved.StateReason);
        Assert.True(saved.HasScriptFile);
        Assert.NotNull(await store.LoadDocumentAsync(id, default));

        // 이유 없는 글이 "Failed: …" 로 시작해도 이유로 읽히지 않는다.
        await store.SaveAsync(saved with { State = RollbackScriptState.Incomplete, Reason = NotCreatedReason.None, StateReason = "Failed: 서버" }, null, default);
        var text = (await store.GetAsync(id, default))!;
        Assert.Equal(NotCreatedReason.None, text.Reason);
        Assert.Equal("Failed: 서버", text.StateReason);
    }

    [Fact]
    public async Task File_write_failure_leaves_no_row()
    {
        using var store = Store();
        await File.WriteAllBytesAsync(RollbackDir, [0]);   // 폴더 자리에 파일이 있어 달 폴더를 만들 수 없다.

        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(Group(_time.GetUtcNow()), Document(), default));

        Assert.Empty(await store.SearchAsync(new RollbackQuery(), default));
    }

    [Fact]
    public async Task Search_filters_by_text_db_and_table_most_recent_first()
    {
        using var store = Store();
        var t0 = _time.GetUtcNow();
        var emp = await store.SaveAsync(Group(t0, table: "EMP"), null, default);
        var dept = await store.SaveAsync(Group(t0.AddMinutes(5), table: "DEPT"), null, default);
        await store.SaveAsync(Group(t0.AddMinutes(9)) with { DbKey = "OTHER/DB/APP" }, null, default);

        var all = await store.SearchAsync(new RollbackQuery { DbKey = Db }, default);
        Assert.Equal([dept, emp], all.Select(g => g.Id));

        // 최근 순 = 끝난 시각(없으면 시작 시각) 순: 일찍 시작해 늦게 끝난 묶음이 앞에 온다.
        var late = await store.SaveAsync(Group(t0.AddMinutes(-30), table: "BONUS") with { EndedAt = t0.AddMinutes(20) }, null, default);
        Assert.Equal(late, (await store.SearchAsync(new RollbackQuery { DbKey = Db, Limit = 1 }, default))[0].Id);

        Assert.Equal(dept, Assert.Single(await store.SearchAsync(new RollbackQuery { Text = "dept" }, default)).Id);
        Assert.Equal(emp, Assert.Single(await store.SearchAsync(
            new RollbackQuery { Table = new DbObjectRef("APP", "EMP", "TABLE"), DbKey = Db }, default)).Id);
    }

    [Fact]
    public async Task Mark_orphans_turns_open_groups_into_unverified()
    {
        using var store = Store();
        var open = await store.SaveAsync(Group(_time.GetUtcNow(), RollbackScriptState.Open) with { EndedAt = null, EndKind = null, EndSource = null }, Document(), default);
        var done = await store.SaveAsync(Group(_time.GetUtcNow()), null, default);
        var thisRun = await store.SaveAsync(Group(_time.GetUtcNow().AddHours(2), RollbackScriptState.Open), null, default);

        Assert.Equal(1, await store.MarkOrphansAsync(default));
        Assert.Equal(RollbackScriptState.Open, (await store.GetAsync(thisRun, default))!.State);

        var marked = (await store.GetAsync(open, default))!;
        Assert.Equal(RollbackScriptState.Unverified, marked.State);
        Assert.Equal(TransactionEndKind.Unknown, marked.EndKind);
        Assert.Equal(TransactionEndSource.AppRestart, marked.EndSource);
        Assert.Equal(SqliteRollbackStore.OrphanReason, marked.StateReason);
        Assert.Equal(RollbackScriptState.Confirmed, (await store.GetAsync(done, default))!.State);
    }

    [Fact]
    public async Task Prune_deletes_expired_groups_with_files_and_orphan_files()
    {
        using var store = Store();
        var now = _time.GetUtcNow();
        var old = await store.SaveAsync(Group(now.AddDays(-40)), Document(), default);
        var recent = await store.SaveAsync(Group(now.AddDays(-2)), Document(), default);
        var oldOpen = await store.SaveAsync(Group(now.AddDays(-50), RollbackScriptState.Open) with { EndedAt = null }, Document(), default);
        var orphan = Path.Combine(RollbackDir, "2026-01", "00009999.orb");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        await File.WriteAllBytesAsync(orphan, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphan, now.UtcDateTime.AddDays(-2));
        var young = Path.Combine(RollbackDir, "2026-11", "00008888.orb");
        await File.WriteAllBytesAsync(young, [4]);
        File.SetLastWriteTimeUtc(young, now.UtcDateTime.AddHours(-1));
        var oldFile = Path.Combine(RollbackDir, "2026-10", old.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".orb");
        Assert.True(File.Exists(oldFile));

        await store.PruneAsync(30, default);

        Assert.Null(await store.GetAsync(old, default));
        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(orphan));
        Assert.False(Directory.Exists(Path.GetDirectoryName(orphan)));
        Assert.True(File.Exists(young));
        Assert.NotNull(await store.LoadDocumentAsync(recent, default));
        Assert.NotNull(await store.LoadDocumentAsync(oldOpen, default));

        // 0일 = 「모두 지우기」: 끝난 묶음은 모두 지우고 열린 묶음은 남긴다.
        await store.PruneAsync(0, default);
        Assert.Null(await store.GetAsync(recent, default));
        Assert.Equal(oldOpen, Assert.Single(await store.SearchAsync(new RollbackQuery(), default)).Id);
    }

    [Fact]
    public async Task Volatile_vault_saves_rows_but_no_file()
    {
        using var vault = new RollbackVault(new VolatileCredentialStore());
        using var store = Store(vault);

        Assert.False(store.CanStoreScripts);
        var id = await store.SaveAsync(Group(_time.GetUtcNow()), Document(), default);

        Assert.False((await store.GetAsync(id, default))!.HasScriptFile);
        Assert.Null(await store.LoadDocumentAsync(id, default));
        Assert.False(Directory.Exists(RollbackDir));
    }

    [Fact]
    public async Task File_moved_to_another_group_slot_does_not_open()
    {
        using var store = Store();
        var first = await store.SaveAsync(Group(_time.GetUtcNow()), Document(), default);
        var second = await store.SaveAsync(Group(_time.GetUtcNow()), Document(), default);
        string Path8(long id) => Path.Combine(RollbackDir, "2026-11", id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".orb");

        File.Copy(Path8(first), Path8(second), overwrite: true);

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => store.LoadDocumentAsync(second, default));
    }

    private sealed class FakeRedactor : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText.Replace(Secret, "***", StringComparison.Ordinal);

        public bool IsSensitiveBindName(string bindName) => false;
    }
}
