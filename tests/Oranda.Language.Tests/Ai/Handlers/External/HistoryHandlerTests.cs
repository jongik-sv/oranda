using System.Text.Json.Nodes;
using Oranda.Core.History;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Language.Ai.Literals;

namespace Oranda.Language.Tests.Ai.Handlers.External;

public class HistoryHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(9));

    private readonly FakeHistory _history = new();

    public HistoryHandlerTests()
    {
        _history.Entries.AddRange(
        [
            Entry(1, ExternalFakes.DevId, "SELECT * FROM emp WHERE id = 1", HistorySource.Editor, 0),
            Entry(2, ExternalFakes.DevId, "SELECT * FROM emp", HistorySource.Agent, 1, error: "ORA-00942"),
            Entry(3, ExternalFakes.ProdId, "SELECT * FROM cust WHERE nm = 'KIM' /* vip */", HistorySource.Editor, 2),
            Entry(4, ExternalFakes.OffId, "SELECT 'off' FROM dual", HistorySource.Editor, 3),
            Entry(5, Guid.NewGuid(), "SELECT 'other' FROM dual", HistorySource.Editor, 4),
        ]);
    }

    private static HistoryEntry Entry(long id, Guid profile, string sql, HistorySource source, int minutes, string? error = null) => new()
    {
        Id = id,
        ProfileId = profile,
        ExecutedAt = T0.AddMinutes(minutes),
        SqlText = sql,
        Source = source,
        Status = error is null ? HistoryStatus.Succeeded : HistoryStatus.Failed,
        ElapsedMs = 12,
        ErrorCode = error,
    };

    private GetHistoryHandler Handler() => new(_history, new SqlLiteralMasker());

    [Fact]
    public async Task History_returns_only_granted_connections_with_metadata_level_newest_first()
    {
        var grant = ExternalFakes.Grant(ExternalFakes.Dev(), ExternalFakes.Prod(), ExternalFakes.Off());

        var output = await Handler().ExecuteAsync(ExternalFakes.App([], grant), CancellationToken.None);

        var entries = output.Result!["entries"]!.AsArray();
        Assert.Equal(["prd-erp", "dev-erp", "dev-erp"], entries.Select(e => e!["connection"]!.GetValue<string>()));
        Assert.Equal("agent", entries[1]!["source"]!.GetValue<string>());
        Assert.Equal("failed", entries[1]!["status"]!.GetValue<string>());
        Assert.Equal("ORA-00942", entries[1]!["error_code"]!.GetValue<string>());
        Assert.Equal(12, entries[1]!["elapsed_ms"]!.GetValue<long>());
        Assert.Equal("SELECT * FROM emp WHERE id = 1", entries[2]!["sql_text"]!.GetValue<string>());
        Assert.DoesNotContain(_history.Queries, q => q.ProfileId == ExternalFakes.OffId);
    }

    [Fact]
    public async Task History_masks_literals_and_comments_on_production()
    {
        var grant = ExternalFakes.Grant(ExternalFakes.Prod());

        var output = await Handler().ExecuteAsync(ExternalFakes.App([], grant, ExternalFakes.Prod()), CancellationToken.None);

        var sql = Assert.Single(output.Result!["entries"]!.AsArray())!["sql_text"]!.GetValue<string>();
        Assert.DoesNotContain("KIM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vip", sql, StringComparison.Ordinal);
        Assert.Contains("FROM cust", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_filters_by_connection_source_since_and_limit()
    {
        var grant = ExternalFakes.Grant(ExternalFakes.Dev(), ExternalFakes.Prod());

        var agentOnly = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["source"] = "Agent" }, grant), CancellationToken.None);
        var since = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["since"] = T0.AddMinutes(1).ToString("O") }, grant, ExternalFakes.Dev()), CancellationToken.None);
        var one = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["limit"] = 1 }, grant), CancellationToken.None);

        Assert.Equal("agent", Assert.Single(agentOnly.Result!["entries"]!.AsArray())!["source"]!.GetValue<string>());
        Assert.Single(since.Result!["entries"]!.AsArray());
        Assert.Equal("prd-erp", Assert.Single(one.Result!["entries"]!.AsArray())!["connection"]!.GetValue<string>());
    }

    [Fact]
    public async Task History_refuses_bad_arguments_and_grants_without_metadata()
    {
        var bad = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["source"] = "robot" }), CancellationToken.None);
        var badSince = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["since"] = "yesterday" }), CancellationToken.None);
        var off = await Handler().ExecuteAsync(ExternalFakes.App([], ExternalFakes.Grant(ExternalFakes.Off())), CancellationToken.None);
        var none = await Handler().ExecuteAsync(ExternalFakes.App([], ExternalFakes.Grant()), CancellationToken.None);

        Assert.Equal("invalid_arguments", bad.ErrorCode);
        Assert.Equal("invalid_arguments", badSince.ErrorCode);
        Assert.Equal("level", off.ErrorCode);
        Assert.Equal("level", none.ErrorCode);
    }

    [Fact]
    public async Task History_clips_long_sql_text()
    {
        _history.Entries.Add(Entry(9, ExternalFakes.DevId, "SELECT " + new string('a', 5000) + " FROM dual", HistorySource.Editor, 10));

        var output = await Handler().ExecuteAsync(ExternalFakes.App(new JsonObject { ["limit"] = 1 }), CancellationToken.None);

        var entry = Assert.Single(output.Result!["entries"]!.AsArray())!;
        Assert.True(entry["sql_text"]!.GetValue<string>().Length <= GetHistoryHandler.MaxSqlChars + 1);
        Assert.True(entry["sql_truncated"]!.GetValue<bool>());
    }
}
