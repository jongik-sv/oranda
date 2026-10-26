using System.Text;
using Microsoft.Data.Sqlite;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Core.Text;
using Oranda.Platform.History;
using Oranda.Platform.Storage;
using Oranda.Testing;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-06-06. 이력 저장·검색·보존·기록기.
public sealed class HistoryStoreTests : IDisposable
{
    private const string Secret = "Hunter2xyz";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-hist-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _p1 = Guid.NewGuid();
    private readonly Guid _p2 = Guid.NewGuid();

    public HistoryStoreTests() => Directory.CreateDirectory(_dir);

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

    private SqliteHistoryStore Store() => SqliteHistoryStore.ForFile(DbFile, new FakeRedactor());

    private HistoryEntry Entry(string sql, Guid? profile = null, DateTimeOffset? at = null, HistoryStatus status = HistoryStatus.Succeeded)
        => new()
        {
            ProfileId = profile ?? _p1,
            ProfileName = "p",
            DbKey = "FREE/FREEPDB1/APP",
            ExecutedAt = at ?? DateTimeOffset.UtcNow,
            SqlText = sql,
            StatementKind = StatementKind.Query,
            Source = HistorySource.Editor,
            Status = status,
            ElapsedMs = 12,
            RowsAffected = 3,
        };

    [Fact]
    public async Task Added_entry_round_trips()
    {
        using var store = Store();
        var at = new DateTimeOffset(2026, 10, 1, 3, 4, 5, 678, TimeSpan.Zero);
        var id = await store.AddAsync(Entry("SELECT * FROM emp", at: at) with { ErrorCode = "ORA-00942", ErrorMessage = "line1\nline2", Status = HistoryStatus.Failed }, default);

        var found = Assert.Single(await store.SearchAsync(new HistoryQuery(), default));
        Assert.Equal(id, found.Id);
        Assert.Equal(_p1, found.ProfileId);
        Assert.Equal("SELECT * FROM emp", found.SqlText);
        Assert.Equal(at, found.ExecutedAt);
        Assert.Equal(HistoryStatus.Failed, found.Status);
        Assert.Equal(StatementKind.Query, found.StatementKind);
        Assert.Equal("line1", found.ErrorMessage);
        Assert.Equal(12, found.ElapsedMs);
        Assert.Equal(3, found.RowsAffected);
        Assert.Null(found.BindsJson);
    }

    [Fact]
    public async Task Secrets_are_redacted_before_storage_and_absent_from_file()
    {
        using var store = Store();
        await store.AddAsync(Entry($"ALTER USER a IDENTIFIED BY {Secret}") with { ErrorMessage = $"bad {Secret}" }, default);
        await store.ClearAsync(profileId: Guid.NewGuid(), default); // 다른 프로필: 아무것도 안 지움
        SqliteConnection.ClearAllPools();

        var found = Assert.Single(await store.SearchAsync(new HistoryQuery(), default));
        Assert.DoesNotContain(Secret, found.SqlText, StringComparison.Ordinal);
        Assert.Contains("***", found.SqlText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, found.ErrorMessage, StringComparison.Ordinal);

        foreach (var file in Directory.GetFiles(_dir))
        {
            var bytes = await ReadSharedAsync(file);
            Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)) >= 0, file);
        }
    }

    private static async Task<byte[]> ReadSharedAsync(string file)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Search_finds_substrings_case_insensitively_and_short_terms()
    {
        using var store = Store();
        await store.AddAsync(Entry("SELECT * FROM All_Objects WHERE object_name LIKE 'X%'"), default);
        await store.AddAsync(Entry("UPDATE emp SET sal = 1 WHERE id = 7"), default);

        Assert.Single(await store.SearchAsync(new HistoryQuery { Text = "all_obj" }, default));
        Assert.Single(await store.SearchAsync(new HistoryQuery { Text = "ECT * FR" }, default));
        Assert.Single(await store.SearchAsync(new HistoryQuery { Text = "id" }, default)); // 3자 미만은 LIKE
        Assert.Single(await store.SearchAsync(new HistoryQuery { Text = "'X%'" }, default));
        Assert.Empty(await store.SearchAsync(new HistoryQuery { Text = "no_such_text" }, default));
        Assert.Equal(2, (await store.SearchAsync(new HistoryQuery(), default)).Count);
    }

    [Fact]
    public async Task Search_filters_by_profile_status_time_and_pages_by_id()
    {
        using var store = Store();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await store.AddAsync(Entry($"SELECT {i} FROM dual", i == 4 ? _p2 : _p1, t0.AddDays(i), i == 2 ? HistoryStatus.Failed : HistoryStatus.Succeeded), default));
        }

        var all = await store.SearchAsync(new HistoryQuery(), default);
        Assert.Equal(ids.AsEnumerable().Reverse(), all.Select(e => e.Id)); // 최신 먼저

        Assert.Equal(4, (await store.SearchAsync(new HistoryQuery { ProfileId = _p1 }, default)).Count);
        Assert.Equal([ids[2]], (await store.SearchAsync(new HistoryQuery { Status = HistoryStatus.Failed }, default)).Select(e => e.Id));
        Assert.Equal(3, (await store.SearchAsync(new HistoryQuery { From = t0.AddDays(1), To = t0.AddDays(3) }, default)).Count);

        var page1 = await store.SearchAsync(new HistoryQuery { Limit = 2 }, default);
        var page2 = await store.SearchAsync(new HistoryQuery { Limit = 2, BeforeId = page1[^1].Id }, default);
        Assert.Equal([ids[4], ids[3]], page1.Select(e => e.Id));
        Assert.Equal([ids[2], ids[1]], page2.Select(e => e.Id));
    }

    [Fact]
    public async Task Delete_and_clear_remove_rows_and_search_index()
    {
        using var store = Store();
        var a = await store.AddAsync(Entry("SELECT 'alpha_row' FROM dual"), default);
        await store.AddAsync(Entry("SELECT 'beta_row' FROM dual", _p2), default);
        await store.AddAsync(Entry("SELECT 'gamma_row' FROM dual"), default);

        await store.DeleteAsync([a], default);
        Assert.Empty(await store.SearchAsync(new HistoryQuery { Text = "alpha_row" }, default));

        await store.ClearAsync(_p1, default);
        await store.PendingVacuum;
        var rest = Assert.Single(await store.SearchAsync(new HistoryQuery(), default));
        Assert.Equal(_p2, rest.ProfileId);

        await store.ClearAsync(null, default);
        await store.PendingVacuum;
        Assert.Empty(await store.SearchAsync(new HistoryQuery(), default));
    }

    [Fact]
    public async Task Prune_applies_age_and_count_limits()
    {
        using var store = Store();
        var now = DateTimeOffset.UtcNow;
        await store.AddAsync(Entry("SELECT 'old' FROM dual", at: now.AddDays(-400)), default);
        for (var i = 0; i < 5; i++)
        {
            await store.AddAsync(Entry($"SELECT {i} FROM dual", at: now.AddMinutes(-10 + i)), default);
        }

        await store.PruneAsync(new HistorySettings { MaxAgeDays = 180, MaxEntries = 3 }, default);

        var left = await store.SearchAsync(new HistoryQuery(), default);
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain(left, e => e.SqlText.Contains("old", StringComparison.Ordinal));
        Assert.Contains("4", left[0].SqlText, StringComparison.Ordinal); // 가장 새것은 남는다
    }

    [Fact]
    public async Task Oversized_sql_is_truncated_on_a_character_boundary()
    {
        using var store = Store();
        var big = new string('가', SqliteHistoryStore.MaxSqlBytes / 3 + 100); // UTF-8 3바이트 글자

        await store.AddAsync(Entry(big), default);

        var found = Assert.Single(await store.SearchAsync(new HistoryQuery(), default));
        Assert.True(found.SqlTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(found.SqlText) <= SqliteHistoryStore.MaxSqlBytes);
        Assert.All(found.SqlText, c => Assert.Equal('가', c));
    }

    [Fact]
    public async Task Long_error_message_is_limited()
    {
        using var store = Store();

        await store.AddAsync(Entry("SELECT 1 FROM dual") with { ErrorMessage = new string('e', 5000) }, default);

        Assert.Equal(1000, (await store.SearchAsync(new HistoryQuery(), default))[0].ErrorMessage!.Length);
    }

    [Fact]
    public async Task Newer_database_is_read_only()
    {
        using (var seed = Store())
        {
            await seed.AddAsync(Entry("SELECT 'kept' FROM dual"), default);
        }

        SqliteConnection.ClearAllPools();
        await using (var c = new SqliteConnection($"Data Source={DbFile};Pooling=False"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO schema_version(version, applied_at, description) VALUES (999, 'x', 'future')";
            await cmd.ExecuteNonQueryAsync();
        }

        using var store = Store();
        Assert.Equal(0, await store.AddAsync(Entry("SELECT 'dropped' FROM dual"), default));
        var rows = await store.SearchAsync(new HistoryQuery(), default);
        Assert.False(store.IsWritable);
        Assert.NotNull(store.Notice);
        Assert.Equal("SELECT 'kept' FROM dual", Assert.Single(rows).SqlText);
    }

    // ---- 기록기 ----

    [Fact]
    public async Task Recorder_stores_executed_statements_and_skips_internal_ones()
    {
        using var store = Store();
        var (recorder, bus, session) = await StartRecorderAsync(store, new HistorySettings());
        var stmt = new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query);
        var result = new ExecutionResult(stmt, ExecutionStatus.Succeeded) { Elapsed = TimeSpan.FromMilliseconds(42), RowsAffected = 5 };

        bus.Publish(new StatementExecuted(session.Id, stmt, result, new ExecutionOptions { Source = HistorySource.Script }));
        bus.Publish(new StatementExecuted(session.Id, stmt, result, new ExecutionOptions { SkipHistory = true }));
        bus.Publish(new StatementExecuted(session.Id, stmt with { Text = "DELETE FROM t" },
            new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") }, ExecutionOptions.Default));
        await recorder.DrainAsync();

        var rows = await store.SearchAsync(new HistoryQuery(), default);
        Assert.Equal(2, rows.Count);
        var ok = rows.Single(r => r.Status == HistoryStatus.Succeeded);
        Assert.Equal(HistorySource.Script, ok.Source);
        Assert.Equal(42, ok.ElapsedMs);
        Assert.Equal(session.Profile.Id, ok.ProfileId);
        Assert.Equal(session.DbKey, ok.DbKey);
        var failed = rows.Single(r => r.Status == HistoryStatus.Failed);
        Assert.Equal("ORA-00942", failed.ErrorCode);
        Assert.Null(failed.BindsJson);
    }

    [Fact]
    public async Task Recorder_records_nothing_when_disabled()
    {
        using var store = Store();
        var (recorder, bus, session) = await StartRecorderAsync(store, new HistorySettings { Enabled = false });
        var stmt = new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query);

        bus.Publish(new StatementExecuted(session.Id, stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded), ExecutionOptions.Default));
        await recorder.DrainAsync();

        Assert.Empty(await store.SearchAsync(new HistoryQuery(), default));
    }

    [Fact]
    public async Task Recorder_saves_bind_values_only_when_setting_is_on_and_masks_secret_names()
    {
        var stmt = new SqlStatement("SELECT :id, :password FROM dual", default, StatementKind.Query);
        HistoryRecorder.BindValuesProvider = _ => new Dictionary<string, string?> { ["id"] = "7", ["password"] = "Pw!" };
        try
        {
            using var off = Store();
            var (offRecorder, offBus, offSession) = await StartRecorderAsync(off, new HistorySettings { SaveBindValues = false });
            offBus.Publish(new StatementExecuted(offSession.Id, stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded), ExecutionOptions.Default));
            await offRecorder.DrainAsync();
            Assert.Null((await off.SearchAsync(new HistoryQuery(), default))[0].BindsJson);

            await off.ClearAsync(null, default);
            var (onRecorder, onBus, onSession) = await StartRecorderAsync(off, new HistorySettings { SaveBindValues = true });
            onBus.Publish(new StatementExecuted(onSession.Id, stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded), ExecutionOptions.Default));
            await onRecorder.DrainAsync();
            var json = (await off.SearchAsync(new HistoryQuery(), default))[0].BindsJson;
            Assert.NotNull(json);
            Assert.Contains("\"7\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Pw!", json, StringComparison.Ordinal);
        }
        finally
        {
            HistoryRecorder.BindValuesProvider = null;
        }
    }

    [Fact]
    public async Task Recorder_saves_the_bind_values_a_run_used_from_its_options_when_the_setting_is_on()
    {
        var stmt = new SqlStatement("SELECT :id, :password FROM dual", default, StatementKind.Query) { BindNames = ["id", "password", "result"] };
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["ID"] = new(BindDataType.Number, 7.5m),
            ["password"] = new(BindDataType.Varchar2, "Pw!"),
            ["result"] = new(BindDataType.Varchar2, null, BindDirection.Out),
            ["other"] = new(BindDataType.Varchar2, "used by another statement of the script"),
        };
        var options = new ExecutionOptions { Source = HistorySource.Editor, Binds = binds }; // 실행 화면이 넘기는 바인드(연결 지점 없이)

        using var store = Store();
        var (offRecorder, offBus, offSession) = await StartRecorderAsync(store, new HistorySettings { SaveBindValues = false });
        offBus.Publish(new StatementExecuted(offSession.Id, stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded), options));
        await offRecorder.DrainAsync();
        Assert.Null((await store.SearchAsync(new HistoryQuery(), default))[0].BindsJson); // 끄면 비운다

        await store.ClearAsync(null, default);
        var (onRecorder, onBus, onSession) = await StartRecorderAsync(store, new HistorySettings { SaveBindValues = true });
        onBus.Publish(new StatementExecuted(onSession.Id, stmt, new ExecutionResult(stmt, ExecutionStatus.Succeeded), options));
        await onRecorder.DrainAsync();

        var json = (await store.SearchAsync(new HistoryQuery(), default))[0].BindsJson;
        Assert.NotNull(json);
        Assert.Contains("\"id\":\"7.5\"", json, StringComparison.Ordinal); // 문장의 이름으로, 문화권 없이
        Assert.Contains("\"password\":\"***\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Pw!", json, StringComparison.Ordinal);
        Assert.DoesNotContain("result", json, StringComparison.Ordinal); // OUT 바인드는 값이 아니다
        Assert.DoesNotContain("another statement", json, StringComparison.Ordinal); // 이 문장이 쓰지 않는 바인드는 뺀다
    }

    private static async Task<(HistoryRecorder Recorder, FakeEventBus Bus, FakeSession Session)> StartRecorderAsync(IHistoryStore store, HistorySettings settings)
    {
        var bus = new FakeEventBus();
        var manager = new FakeSessionManager();
        var profile = TestData.Profile() with { Id = Guid.NewGuid() };
        var session = (FakeSession)await manager.OpenAsync(profile, new SecureCredential("x"), SessionPurpose.Work, default);
        var services = new ServiceMap(new Dictionary<Type, object>
        {
            [typeof(ISessionManager)] = manager,
            [typeof(ISecretRedactor)] = new FakeRedactor(),
        });
        var recorder = new HistoryRecorder(bus, store, new FixedSettings(settings), services);
        await recorder.RunAsync(default);
        return (recorder, bus, session);
    }

    private sealed class FakeRedactor : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText.Replace(Secret, "***", StringComparison.Ordinal);

        public bool IsSensitiveBindName(string bindName) => bindName.Contains("pass", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedSettings(HistorySettings history) : ISettingsStore
    {
        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct)
            => Task.FromResult(new StoredDocument<AppSettings>(new AppSettings { History = history }));

        public Task SaveAsync(AppSettings settings, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ServiceMap(Dictionary<Type, object> map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => map.GetValueOrDefault(serviceType);
    }
}
