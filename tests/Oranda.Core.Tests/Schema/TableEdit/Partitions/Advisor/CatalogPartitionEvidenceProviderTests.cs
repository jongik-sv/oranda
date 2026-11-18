using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Sessions;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 공급자 뼈대: 질의 id·바꿈 값·바인드가 맞게 나가는지, 받은 줄이 근거로 바뀌는지, 오류가 그대로 올라가는지. DBMS 쪽 문장은 각 IT 가 본다.
public sealed class CatalogPartitionEvidenceProviderTests
{
    private static readonly ISession NoSession = null!; // 가짜 공급자는 세션을 쓰지 않는다

    private sealed class FakeProvider : CatalogPartitionEvidenceProvider
    {
        public List<(string Id, IReadOnlyDictionary<string, string> Replacements, IReadOnlyDictionary<string, object?> Binds)> Sent { get; } = [];

        public Dictionary<string, IReadOnlyList<object?[]>> Answers { get; } = [];

        public Exception? Fail { get; set; }

        protected override DateTime Now => new(2026, 11, 18);

        protected override Task<IReadOnlyList<object?[]>> QueryAsync(
            ISession s, string id, IReadOnlyDictionary<string, string> replacements, IReadOnlyDictionary<string, object?> binds, CancellationToken ct)
        {
            Sent.Add((id, replacements, binds));
            if (Fail is not null && id == "advisor.predicate_plans")
            {
                return Task.FromException<IReadOnlyList<object?[]>>(Fail);
            }

            return Task.FromResult(Answers.TryGetValue(id, out var rows) ? rows : []);
        }
    }

    [Fact]
    public async Task Predicate_rows_of_one_statement_are_merged_and_weighted_by_executions()
    {
        var p = new FakeProvider();
        p.Answers["advisor.table_columns"] = [["ORDER_DT"], ["STATUS"], ["ID"]];
        p.Answers["advisor.predicate_plans"] =
        [
            ["s1", 90m, "\"ORDER_DT\">=:1", null],                 // 인덱스 줄
            ["s1", 90m, null, "\"T\".\"ORDER_DT\"<:2"],            // 표 줄 — 같은 문장은 한 번만 센다
            ["s2", 10, null, "\"STATUS\"='X' AND \"ID\"=1"],
        ];

        var use = (await p.GetPredicateUseAsync(NoSession, "SAMPLE", "ORDERS", default))!;

        Assert.Equal(["ORDER_DT", "ID", "STATUS"], use.Select(u => u.Column));
        Assert.Equal((90L, 1), (use[0].Executions, use[0].Statements));
        Assert.Equal(0.9, use[0].Share, 6);
        Assert.Equal(0.1, use[1].Share, 6); // 실행 수가 같으면 이름 순
        var plans = p.Sent.Single(x => x.Id == "advisor.predicate_plans");
        Assert.Equal("200", plans.Replacements["@MAX@"]);
        Assert.Equal("SAMPLE", plans.Binds["owner"]);
        Assert.Equal("ORDERS", plans.Binds["name"]);
    }

    [Fact]
    public async Task A_table_with_no_visible_columns_has_no_predicates_and_never_reads_the_plan_cache()
    {
        var p = new FakeProvider();

        var use = await p.GetPredicateUseAsync(NoSession, "SAMPLE", "GONE", default);

        Assert.Empty(use!);
        Assert.DoesNotContain(p.Sent, x => x.Id == "advisor.predicate_plans");
    }

    [Fact]
    public async Task A_privilege_error_from_the_plan_cache_goes_up_unchanged()
    {
        var p = new FakeProvider { Fail = new InvalidOperationException("ORA-00942") };
        p.Answers["advisor.table_columns"] = [["C"]];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.GetPredicateUseAsync(NoSession, "SAMPLE", "T", default));

        Assert.Equal("ORA-00942", ex.Message);
    }

    [Fact]
    public async Task Date_like_detection_samples_big_tables_and_leaves_small_ones_whole()
    {
        var p = new FakeProvider();
        p.Answers["advisor.table_rows"] = [[1_000_000m]];
        p.Answers["advisor.date_sample"] = [["20260101"], ["20260215"], [null]];

        var like = await p.DetectDateLikeAsync(NoSession, "SAMPLE", "BIG", "D", default);
        var small = new FakeProvider();
        small.Answers["advisor.table_rows"] = [[50m]];
        await small.DetectDateLikeAsync(NoSession, "SAMPLE", "SMALL", "D", default);

        Assert.Equal(DateLikeFormat.Yyyymmdd, like!.Format);
        Assert.Equal(2, like.SampleSize);
        var q = p.Sent.Single(x => x.Id == "advisor.date_sample");
        Assert.Equal("\"SAMPLE\".\"BIG\"", q.Replacements["@TABLE@"]);
        Assert.Equal("\"D\"", q.Replacements["@COL@"]);
        Assert.Equal(" SAMPLE (5)", q.Replacements["@SAMPLE@"]);
        Assert.Equal(string.Empty, small.Sent.Single(x => x.Id == "advisor.date_sample").Replacements["@SAMPLE@"]);
    }

    [Fact]
    public async Task Monthly_builds_the_label_expression_and_the_distribution()
    {
        var p = new FakeProvider();
        p.Answers["advisor.monthly"] = [["2026-11", 7L], ["2026-10", 3m], [null, 2L], ["1999-01", 1L]];

        var d = await p.SampleMonthlyAsync(NoSession, "SAMPLE", "T", "YMD", DateLikeFormat.Yyyymmdd, 10, default);

        Assert.Equal(["2026-10", "2026-11"], d!.Buckets.Select(b => b.Label));
        Assert.Equal(1, d.RestCount);
        Assert.Equal(2, d.NullCount);
        Assert.Equal(13, d.SampledRows);
        Assert.Equal(10, d.SamplePercent);
        var q = p.Sent.Single();
        Assert.Contains("SUBSTR(TO_CHAR(\"YMD\"), 1, 4)", q.Replacements["@LABEL@"], StringComparison.Ordinal);
        Assert.Equal(" SAMPLE (10)", q.Replacements["@SAMPLE@"]);
    }

    [Fact]
    public async Task Names_are_quoted_so_a_quote_in_a_name_cannot_break_out()
    {
        var p = new FakeProvider();

        await p.SampleMonthlyAsync(NoSession, "O\"WN", "T\"B", "C\"L", null, null, default);

        var q = p.Sent.Single();
        Assert.Equal("\"O\"\"WN\".\"T\"\"B\"", q.Replacements["@TABLE@"]);
        Assert.Equal("TO_CHAR(\"C\"\"L\", 'YYYY-MM')", q.Replacements["@LABEL@"]);
    }

    [Fact]
    public async Task Top_values_read_totals_from_the_first_row_and_an_empty_table_gives_an_empty_distribution()
    {
        var p = new FakeProvider();
        p.Answers["advisor.top_values"] = [["A", 50L, 100L, 5L], ["B", 30L, 100L, 5L], [null, 5L, 100L, 5L]];

        var d = await p.SampleTopValuesAsync(NoSession, "SAMPLE", "T", "S", 2, null, default);
        var empty = await new FakeProvider().SampleTopValuesAsync(NoSession, "SAMPLE", "T", "S", 2, null, default);

        Assert.Equal([("A", 50L), ("B", 30L)], d!.Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal(15, d.RestCount);
        Assert.Equal(5, d.NullCount);
        Assert.Equal(100, d.SampledRows);
        Assert.Equal("2", p.Sent.Single().Replacements["@TOP@"]);
        Assert.Empty(empty!.Buckets);
        Assert.Equal(0, empty.SampledRows);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => p.SampleTopValuesAsync(NoSession, "SAMPLE", "T", "S", 0, null, default));
    }
}
