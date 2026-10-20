using Microsoft.Data.Sqlite;
using Oranda.Core.Events;
using Oranda.Core.Journal;
using Oranda.Core.Security;
using Oranda.Platform.Journal;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Journal;

// 소유: TSK-13-07. 변경 일지 저장소·기록기·마이그레이션 0002.
public sealed class ChangeJournalStoreTests : IDisposable
{
    private const string Secret = "Hunter2xyz";
    private const string Db = "FREE/FREEPDB1/APP";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-journal-" + Guid.NewGuid().ToString("N"));

    public ChangeJournalStoreTests() => Directory.CreateDirectory(_dir);

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

    private SqliteChangeJournalStore Store() => SqliteChangeJournalStore.ForFile(DbFile, new FakeRedactor());

    private static JournalEntry Entry(string ddl, string name = "EMP", string type = "TABLE", DateTimeOffset? at = null)
        => new()
        {
            CapturedAt = at ?? DateTimeOffset.UtcNow,
            ProfileId = Guid.NewGuid(),
            ProfileName = "p",
            DbKey = Db,
            ServerName = "host/FREEPDB1",
            Owner = "APP",
            Name = name,
            ObjectType = type,
            Cause = JournalCause.Ddl,
            StatementSummary = "ALTER TABLE EMP ADD x NUMBER",
            PreviousDdl = ddl,
        };

    [Fact]
    public async Task Migration_0002_creates_table_and_is_safe_to_reapply()
    {
        using (var first = Store())
        {
            await first.AddAsync(Entry("CREATE TABLE emp (id NUMBER)"), default);
            Assert.True(first.IsWritable);
        }

        SqliteConnection.ClearAllPools();
        await using var connection = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default);
        Assert.Equal(OrandaDatabase.LatestVersion, await OrandaDatabase.GetVersionAsync(connection, default));
        Assert.True(OrandaDatabase.LatestVersion >= 2);

        // 이미 적용된 DB 에 다시 적용해도 그대로(행이 남아 있다).
        var again = await OrandaDatabase.MigrateAsync(connection, null, default);
        Assert.True(again.Writable);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM change_journal";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Migration_upgrades_a_version_1_database_keeping_history()
    {
        await using (var seed = await OrandaDatabase.OpenConnectionAsync(OrandaDatabase.FileConnectionString(DbFile), default))
        {
            await using var cmd = seed.CreateCommand();
            cmd.CommandText = "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, description TEXT NOT NULL);"
                + "INSERT INTO schema_version VALUES (1, '2026-01-01T00:00:00.000Z', 'init');";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var result = await OrandaDatabase.PrepareFileAsync(DbFile, Path.Combine(_dir, "backup"), default);
        Assert.True(result.Writable);
        Assert.Equal(OrandaDatabase.LatestVersion, result.Version);
    }

    [Fact]
    public async Task Added_entry_round_trips_and_search_filters()
    {
        using var store = Store();
        var at = new DateTimeOffset(2026, 10, 1, 3, 4, 5, 678, TimeSpan.Zero);
        var id = await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER)", at: at), default);
        await store.AddAsync(Entry("CREATE TABLE dept (id NUMBER)", name: "DEPT"), default);

        var found = await store.GetAsync(id!.Value, default);
        Assert.NotNull(found);
        Assert.Equal(at, found.CapturedAt);
        Assert.Equal("EMP", found.Name);
        Assert.Equal(JournalCause.Ddl, found.Cause);
        Assert.Equal("CREATE TABLE emp (id NUMBER)", found.PreviousDdl);
        Assert.Equal(JournalHash.Compute("CREATE TABLE emp (id NUMBER)"), found.ContentHash);
        Assert.False(found.IsFailure);

        Assert.Equal(2, (await store.SearchAsync(new JournalQuery(), default)).Count);
        Assert.Single(await store.SearchAsync(new JournalQuery { Text = "dep" }, default));
        Assert.Equal(2, (await store.SearchAsync(new JournalQuery { Text = "NUMBER)" }, default)).Count); // DDL 본문도 검색한다.
        Assert.Single(await store.SearchAsync(new JournalQuery { Name = "EMP" }, default));
        Assert.Empty(await store.SearchAsync(new JournalQuery { Text = "no_such" }, default));
        Assert.Equal("DEPT", (await store.SearchAsync(new JournalQuery(), default))[0].Name); // 최신 먼저
    }

    [Fact]
    public async Task Same_hash_as_last_success_is_not_stacked_but_failures_do_not_break_the_chain()
    {
        using var store = Store();
        Assert.NotNull(await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER)"), default));
        Assert.Null(await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER)\r\n"), default)); // 줄바꿈·끝 공백 차이는 같은 내용
        Assert.NotNull(await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER, x NUMBER)"), default));

        var failure = Entry("x") with { PreviousDdl = null, FailureReason = "ORA-31603: object not found" };
        Assert.NotNull(await store.AddAsync(failure, default)); // 실패 행은 쌓인다
        Assert.Null(await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER, x NUMBER)"), default)); // 실패 행이 끼어도 같은 해시는 안 쌓임

        var all = await store.SearchAsync(new JournalQuery(), default);
        Assert.Equal(3, all.Count);
        Assert.Single(all, e => e.IsFailure && e.PreviousDdl is null && e.ContentHash is null);
        Assert.Equal(2, (await store.SearchAsync(new JournalQuery { OnlySucceeded = true }, default)).Count);
    }

    [Fact]
    public async Task Secrets_in_ddl_and_summary_are_redacted_but_stored_source_is_kept()
    {
        using var store = Store();
        await store.AddAsync(Entry($"CREATE USER a IDENTIFIED BY {Secret}", name: "A", type: "USER") with { StatementSummary = $"ALTER USER a IDENTIFIED BY {Secret}" }, default);
        await store.AddAsync(Entry($"PROCEDURE p IS v VARCHAR2(10) := '{Secret}'; BEGIN NULL; END;", name: "P", type: "PROCEDURE"), default);
        SqliteConnection.ClearAllPools();

        var all = await store.SearchAsync(new JournalQuery(), default);
        var user = Assert.Single(all, e => e.ObjectType == "USER");
        Assert.DoesNotContain(Secret, user.PreviousDdl, StringComparison.Ordinal);
        Assert.Contains("***", user.PreviousDdl, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, user.StatementSummary, StringComparison.Ordinal);
        var proc = Assert.Single(all, e => e.ObjectType == "PROCEDURE");
        Assert.Contains(Secret, proc.PreviousDdl, StringComparison.Ordinal); // 소스는 되돌리기 본문이라 가리지 않는다.
    }

    [Fact]
    public async Task Oversized_ddl_becomes_a_failure_row()
    {
        using var store = Store();
        await store.AddAsync(Entry(new string('x', SqliteChangeJournalStore.MaxDdlBytes + 1)), default);

        var row = Assert.Single(await store.SearchAsync(new JournalQuery(), default));
        Assert.True(row.IsFailure);
        Assert.Null(row.PreviousDdl);
    }

    [Fact]
    public async Task Prune_keeps_newest_versions_per_object_and_drops_old_rows()
    {
        using var store = Store();
        for (var i = 0; i < 5; i++)
        {
            await store.AddAsync(Entry($"CREATE TABLE emp (c{i} NUMBER)"), default);
            await store.AddAsync(Entry($"CREATE TABLE dept (c{i} NUMBER)", name: "DEPT"), default);
        }

        await store.AddAsync(Entry("CREATE TABLE old (id NUMBER)", name: "OLD", at: DateTimeOffset.UtcNow.AddDays(-400)), default);
        await store.PruneAsync(new JournalRetention { MaxAgeDays = 365, MaxVersionsPerObject = 3 }, default);

        var all = await store.SearchAsync(new JournalQuery(), default);
        Assert.Equal(3, all.Count(e => e.Name == "EMP"));
        Assert.Equal(3, all.Count(e => e.Name == "DEPT"));
        Assert.DoesNotContain(all, e => e.Name == "OLD");
        Assert.Contains(all, e => e.PreviousDdl!.Contains("c4", StringComparison.Ordinal) && e.Name == "EMP"); // 최신이 남는다
        Assert.DoesNotContain(all, e => e.PreviousDdl!.Contains("c0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Newer_database_version_is_read_only_and_writes_are_skipped()
    {
        using (var seed = Store())
        {
            await seed.SearchAsync(new JournalQuery(), default);
        }

        await using (var c = new SqliteConnection(OrandaDatabase.FileConnectionString(DbFile)))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_version VALUES (999, '2099-01-01T00:00:00.000Z', 'future')";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        using var store = Store();
        Assert.Null(await store.AddAsync(Entry("CREATE TABLE emp (id NUMBER)"), default));
        Assert.False(store.IsWritable);
        Assert.Empty(await store.SearchAsync(new JournalQuery(), default));
    }

    [Fact]
    public async Task Recorder_stores_captured_events_in_order_and_swallows_store_failures()
    {
        using var store = Store();
        var bus = new FakeEventBus();
        var recorder = new ChangeJournalRecorder(bus, store);
        await recorder.RunAsync(default);

        var saved = new List<long>();
        bus.Subscribe<ChangeJournalSaved>(this, e => saved.Add(e.Id));
        bus.Publish(new ChangeSnapshotCaptured(Entry("CREATE TABLE a (id NUMBER)", name: "A")));
        bus.Publish(new ChangeSnapshotCaptured(Entry("CREATE TABLE b (id NUMBER)", name: "B")));
        bus.Publish(new ChangeSnapshotCaptured(Entry("CREATE TABLE b (id NUMBER)", name: "B"))); // 중복: 저장 알림 없음
        await recorder.DrainAsync();
        Assert.Equal(2, saved.Count);
        Assert.Equal(["B", "A"], (await store.SearchAsync(new JournalQuery(), default)).Select(e => e.Name));

        var failing = new ChangeJournalRecorder(bus, new ThrowingStore());
        await failing.RunAsync(default);
        bus.Publish(new ChangeSnapshotCaptured(Entry("x")));
        await failing.DrainAsync(); // 예외 없이 끝난다.
    }

    private sealed class ThrowingStore : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => throw new IOException("disk");

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct) => throw new IOException("disk");

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => throw new IOException("disk");

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => throw new IOException("disk");
    }

    private sealed class FakeRedactor : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText.Replace(Secret, "***", StringComparison.Ordinal);

        public bool IsSensitiveBindName(string bindName) => bindName.Contains("pass", StringComparison.OrdinalIgnoreCase);
    }
}
