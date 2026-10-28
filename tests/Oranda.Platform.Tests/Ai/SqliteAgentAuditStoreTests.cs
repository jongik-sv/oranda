using Microsoft.Data.Sqlite;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Security;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai;

public sealed class SqliteAgentAuditStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-audit-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _p1 = Guid.NewGuid();
    private readonly Guid _p2 = Guid.NewGuid();

    public SqliteAgentAuditStoreTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task Append_then_list_returns_newest_first_and_filters_by_profile()
    {
        using var store = await StoreAsync();
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(Entry("describe_object", _p1, now.AddMinutes(-2)), default);
        await store.AppendAsync(Entry("get_plan", _p2, now.AddMinutes(-1)), default);
        await store.AppendAsync(Entry("run_query", _p1, now), default);

        var all = await store.ListRecentAsync(10, null, default);
        Assert.Equal(["run_query", "get_plan", "describe_object"], all.Select(e => e.Tool));

        var first = await store.ListRecentAsync(10, _p1, default);
        Assert.Equal(["run_query", "describe_object"], first.Select(e => e.Tool));

        var row = all[0];
        Assert.Equal(AiAccessLevel.Query, row.AccessLevel);
        Assert.Equal(AiToolJudgement.Masked, row.Verdict);
        Assert.Equal(_p1, row.ProfileId);
        Assert.Equal("run-1", row.RunId);
        Assert.Equal(42, row.ResultChars);
        Assert.Equal(7, row.ElapsedMs);
        Assert.True(row.Id > 0);
        Assert.Single(await store.ListRecentAsync(1, null, default));
    }

    [Fact]
    public async Task Secrets_in_arguments_and_reason_are_masked_before_storage()
    {
        using var store = await StoreAsync();
        var entry = Entry("run_query", _p1, DateTimeOffset.UtcNow) with
        {
            Arguments = "select 1 from dual -- password=Hunter2xyz; scott/tiger@db token: abc123 Authorization Bearer eyJhbGciOi.abc-def XSecretX",
            Reason = "denied: api_key=\"k e y\" and ORANDA_RUN_TOKEN=zzz",
        };

        await store.AppendAsync(entry, default);

        var saved = (await store.ListRecentAsync(1, null, default)).Single();
        foreach (var leaked in new[] { "Hunter2xyz", "tiger", "abc123", "eyJhbGciOi", "XSecretX", "k e y", "zzz" })
        {
            Assert.DoesNotContain(leaked, saved.Arguments + saved.Reason, StringComparison.Ordinal);
        }

        Assert.Contains("scott/***@", saved.Arguments, StringComparison.Ordinal);
        Assert.Contains("select 1 from dual", saved.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_arguments_header_style_and_prefixed_keys_are_masked()
    {
        using var store = await StoreAsync();
        var now = DateTimeOffset.UtcNow;
        var inputs = new[]
        {
            "Authorization: Bearer eyJhbGciOi.abc-def",
            """{"password":"Hunter2xyz","name":"EMP"}""",
            """{"nested":{"token": "abc123"},"list":["fine", "password=zzz9"]}""",
            "db_password=Hunter2xyz access_token=abc123 client_secret=s3cr3t",
            """{"sql":"select 1 from dual where x = 'XSecretX'"}""",
        };
        for (var i = 0; i < inputs.Length; i++)
        {
            await store.AppendAsync(Entry("t" + i, _p1, now.AddSeconds(i)) with { Arguments = inputs[i] }, default);
        }

        var saved = (await store.ListRecentAsync(10, null, default)).OrderBy(e => e.Tool).Select(e => e.Arguments!).ToList();
        foreach (var leaked in new[] { "eyJhbGciOi", "Hunter2xyz", "abc123", "zzz9", "s3cr3t", "XSecretX" })
        {
            Assert.DoesNotContain(saved, text => text.Contains(leaked, StringComparison.Ordinal));
        }

        Assert.Contains("EMP", saved[1], StringComparison.Ordinal);
        Assert.Contains("fine", saved[2], StringComparison.Ordinal);
        Assert.Contains("select 1 from dual", saved[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Numeric_and_object_arrays_are_kept_and_only_exact_secret_keys_are_masked()
    {
        using var store = await StoreAsync();
        await store.AppendAsync(
            Entry("t", _p1, DateTimeOffset.UtcNow) with
            {
                Arguments = """{"binds":[1,2],"rows":[{"a":1}],"budget_tokens":5,"password":"Hunter2xyz","note":"한글 'quote'"}""",
            },
            default);

        var saved = (await store.ListRecentAsync(1, null, default)).Single();

        Assert.Contains("\"binds\":[1,2]", saved.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"a\":1", saved.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"budget_tokens\":5", saved.Arguments, StringComparison.Ordinal);
        Assert.Contains("한글 'quote'", saved.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2xyz", saved.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_text_is_truncated()
    {
        using var store = await StoreAsync();
        await store.AppendAsync(
            Entry("x", _p1, DateTimeOffset.UtcNow) with { Arguments = new string('a', 5000), Reason = new string('b', 5000) },
            default);

        var saved = (await store.ListRecentAsync(1, null, default)).Single();
        Assert.Equal(SqliteAgentAuditStore.MaxArgumentChars, saved.Arguments!.Length);
        Assert.Equal(SqliteAgentAuditStore.MaxReasonChars, saved.Reason!.Length);
    }

    [Fact]
    public async Task Purge_removes_only_entries_older_than_cutoff()
    {
        using var store = await StoreAsync();
        var now = DateTimeOffset.UtcNow;
        // 첫 쓰기가 자동 정리를 한 번 돌리므로 그 뒤에 오래된 줄을 넣어야 직접 정리를 따로 확인할 수 있다.
        await store.AppendAsync(Entry("recent", _p1, now.AddDays(-1)), default);
        await store.AppendAsync(Entry("old", _p1, now.AddDays(-100)), default);
        Assert.Equal(2, (await store.ListRecentAsync(10, null, default)).Count);

        await store.PurgeAsync(now.AddDays(-SqliteAgentAuditStore.MaxAgeDays), default);

        Assert.Equal(["recent"], (await store.ListRecentAsync(10, null, default)).Select(e => e.Tool));
    }

    [Fact]
    public async Task Append_prunes_entries_older_than_retention_on_first_write()
    {
        using var store = await StoreAsync();
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(Entry("old", _p1, now.AddDays(-100)), default);
        await store.AppendAsync(Entry("recent", _p1, now), default);

        // 첫 쓰기가 끝날 때 한 번 정리하므로 100일 전 줄은 그 쓰기에서 이미 사라진다.
        Assert.Equal(["recent"], (await store.ListRecentAsync(10, null, default)).Select(e => e.Tool));
    }

    private static AgentAuditEntry Entry(string tool, Guid profile, DateTimeOffset at) => new()
    {
        OccurredAt = at,
        ProfileId = profile,
        ProfileName = "p",
        Client = "claude-code",
        RunId = "run-1",
        Tool = tool,
        Arguments = "{\"name\":\"EMP\"}",
        AccessLevel = AiAccessLevel.Query,
        Verdict = AiToolJudgement.Masked,
        Reason = "masked_columns",
        ResultChars = 42,
        ElapsedMs = 7,
    };

    private Task<SqliteAgentAuditStore> StoreAsync() =>
        Task.FromResult(SqliteAgentAuditStore.ForFile(DbFile, new MaskingRedactor()));

    private sealed class MaskingRedactor : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText.Replace("XSecretX", "***", StringComparison.Ordinal);

        public bool IsSensitiveBindName(string bindName) => bindName.Contains("pass", StringComparison.OrdinalIgnoreCase);
    }
}
