using System.Text.Json;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Testing;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core PC1. AI 파티션 추천 계약(설계 2026-11-18 4장)의 꼴과 빈 껍데기 동작.
public class AdvisorContractTests
{
    private static FakeSession TiberoSession() => new(TestData.Profile() with { Dbms = Dbms.Tibero });

    private const string SampleAdvice = """
        {
          "verdict": "partition",
          "summary": "월 단위 범위",
          "candidates": [
            {
              "rank": 1, "title": "ORDER_DT 월 범위", "method": "range", "key_columns": ["ORDER_DT"],
              "interval": "month",
              "layout": { "kind": "monthly", "start": "2024-01", "periods_ahead": 12 },
              "subpartition": { "method": "list", "key_columns": ["STATUS"], "groups": [ { "name": "SP_OPEN", "values": ["V1"], "is_default": false } ] },
              "local_indexes": ["IX_ORDERS_DT"], "reasons": ["조건 70%"], "cautions": []
            }
          ],
          "not_chosen": [ { "column": "CUST_ID", "reason": "고르게 퍼짐" } ]
        }
        """;

    [Fact]
    public void Schema_json_is_an_object_schema_that_requires_the_top_fields()
    {
        var schema = PartitionAdvice.Schema();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(["verdict", "summary", "candidates", "not_chosen"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var methods = schema.GetProperty("properties").GetProperty("candidates").GetProperty("items").GetProperty("properties").GetProperty("method").GetProperty("enum");
        Assert.Equal([AdviceMethod.Range, AdviceMethod.List, AdviceMethod.Hash], methods.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Parser_reads_a_structured_result_in_snake_case()
    {
        using var doc = JsonDocument.Parse(SampleAdvice);

        var advice = PartitionAdviceParser.Parse(doc.RootElement, text: null);

        Assert.NotNull(advice);
        Assert.Equal(AdviceVerdict.Partition, advice.Verdict);
        var c = Assert.Single(advice.Candidates);
        Assert.Equal((1, AdviceMethod.Range, "month"), (c.Rank, c.Method, c.Interval));
        Assert.Equal(["ORDER_DT"], c.KeyColumns);
        Assert.Equal((AdviceLayoutKind.Monthly, "2024-01", 12), (c.Layout.Kind, c.Layout.Start, c.Layout.PeriodsAhead));
        Assert.Equal("SP_OPEN", Assert.Single(c.Subpartition!.Groups!).Name);
        Assert.Equal(["IX_ORDERS_DT"], c.LocalIndexes);
        Assert.Equal("CUST_ID", Assert.Single(advice.NotChosen).Column);
    }

    [Fact]
    public void Parser_fills_missing_lists_with_empty_ones_and_returns_null_for_nothing_or_a_non_object()
    {
        using var doc = JsonDocument.Parse("""{ "verdict": "no_partition", "summary": "작은 표" }""");
        var advice = PartitionAdviceParser.Parse(doc.RootElement, null);

        Assert.NotNull(advice);
        Assert.Empty(advice.Candidates);
        Assert.Empty(advice.NotChosen);

        using var array = JsonDocument.Parse("[1, 2]");
        Assert.Null(PartitionAdviceParser.Parse(array.RootElement, null));
        Assert.Null(PartitionAdviceParser.Parse(null, null));
    }

    [Fact]
    public void Vault_gives_one_token_per_column_value_and_unmasks_only_on_the_same_column()
    {
        var vault = new EvidenceValueVault();

        var a = vault.Mask("STATUS", "OPEN");
        var b = vault.Mask("STATUS", "CLOSED");
        var again = vault.Mask("STATUS", "OPEN");
        var other = vault.Mask("REGION", "OPEN");

        Assert.Equal(("V1", "V2", "V1", "V3"), (a, b, again, other));
        Assert.Equal("OPEN", vault.Unmask("status", "v1"));
        Assert.Equal("OPEN", vault.Unmask("REGION", "V3"));
        Assert.Null(vault.Unmask("REGION", "V1")); // 다른 열의 자리표시자
        Assert.Null(vault.Unmask("STATUS", "V9"));
        Assert.Equal(3, vault.Count);
    }

    [Fact]
    public async Task Unsupported_provider_returns_null_for_every_question()
    {
        var p = UnsupportedPartitionEvidenceProvider.Instance;
        var s = TiberoSession();

        Assert.Null(await p.GetPredicateUseAsync(s, "SAMPLE", "ORDERS", default));
        Assert.Null(await p.DetectDateLikeAsync(s, "SAMPLE", "ORDERS", "ORDER_YMD", default));
        Assert.Null(await p.SampleMonthlyAsync(s, "SAMPLE", "ORDERS", "ORDER_DT", null, null, default));
        Assert.Null(await p.SampleTopValuesAsync(s, "SAMPLE", "ORDERS", "STATUS", 20, 1.5, default));
    }

    [Fact]
    public async Task Dispatcher_forwards_to_the_session_dbms_and_falls_back_to_unsupported()
    {
        var oracle = new MarkerProvider();
        var dispatcher = new PartitionEvidenceProviderDispatcher(new DbmsServiceTable().Add<IPartitionEvidenceProvider>(Dbms.Oracle, () => oracle));

        var uses = await dispatcher.GetPredicateUseAsync(new FakeSession(TestData.Profile()), "SAMPLE", "ORDERS", default);
        Assert.Equal("ORDER_DT", Assert.Single(uses!).Column);

        Assert.Null(await dispatcher.GetPredicateUseAsync(TiberoSession(), "SAMPLE", "ORDERS", default));
        Assert.Same(UnsupportedPartitionEvidenceProvider.Instance, dispatcher.For(TiberoSession()));
    }

    [Fact]
    public async Task Collector_shell_names_the_table_and_session_dbms_and_honours_cancel()
    {
        var collector = new PartitionEvidenceCollector(new FakeMetadataProvider(), new NoTuning(), UnsupportedPartitionEvidenceProvider.Instance);
        var table = new DbObjectRef("SAMPLE", "ORDERS", "TABLE");

        var e = await collector.CollectAsync(TiberoSession(), table, EvidenceOptions.Default, null, default);

        Assert.Equal(("SAMPLE", "ORDERS", Dbms.Tibero), (e.Owner, e.Table, e.Dbms));
        Assert.Empty(e.Columns);
        Assert.Empty(e.Skipped);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(TiberoSession(), table, EvidenceOptions.Default, null, cts.Token));
    }

    [Fact]
    public void Checker_level_rules_and_can_fill()
    {
        Assert.True(PartitionAdviceChecker.CanFill([]));
        Assert.True(PartitionAdviceChecker.CanFill([new AdviceCheck(AdviceCheckLevel.Warn, AdviceCheckCodes.SmallTable)]));
        Assert.False(PartitionAdviceChecker.CanFill([new AdviceCheck(AdviceCheckLevel.Ok, AdviceCheckCodes.LocalOk), new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing)]));
        Assert.Equal("TableEdit.partition.ai.check.key-missing", new AdviceCheck(AdviceCheckLevel.Stop, AdviceCheckCodes.KeyMissing).TextKey);
    }

    [Fact]
    public void Applier_shell_keeps_method_keys_local_indexes_and_subpartition_apart()
    {
        using var doc = JsonDocument.Parse(SampleAdvice);
        var c = PartitionAdviceParser.Parse(doc.RootElement, null)!.Candidates[0];

        var d = PartitionAdviceApplier.Apply(c, Empty(), new EvidenceValueVault(), new DateOnly(2026, 11, 18));

        Assert.Equal(PartitionMethod.Range, d.Draft.Scheme!.Method);
        Assert.Equal(["ORDER_DT"], d.Draft.Scheme.KeyColumns);
        Assert.Equal(["IX_ORDERS_DT"], d.LocalIndexes);
        Assert.Equal(AdviceMethod.List, d.Subpartition!.Method);
    }

    [Fact]
    public void Prompt_shell_names_the_table_and_returns_a_fresh_vault()
    {
        var (prompt, vault) = PartitionAdvicePrompt.Build(Empty(), new AdvicePromptOptions(AdviceGoal.QueryAndPurge, SendValues: false, Language: "ko"));

        Assert.Contains("SAMPLE.ORDERS", prompt, StringComparison.Ordinal);
        Assert.Equal(0, vault.Count);
    }

    private static PartitionEvidence Empty() => PartitionEvidence.Empty("SAMPLE", "ORDERS", Dbms.Oracle, new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local));

    private sealed class MarkerProvider : IPartitionEvidenceProvider
    {
        public Task<IReadOnlyList<PredicateUse>?> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PredicateUse>?>([new PredicateUse("ORDER_DT", 10, 1, 1.0)]);

        public Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct) => Task.FromResult<DateLike?>(null);

        public Task<ColumnDistribution?> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct) =>
            Task.FromResult<ColumnDistribution?>(null);

        public Task<ColumnDistribution?> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct) =>
            Task.FromResult<ColumnDistribution?>(null);
    }

    private sealed class NoTuning : ITuningService
    {
    }
}
