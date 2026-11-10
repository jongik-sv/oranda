using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Connection;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 동시 테이블 수(PF2): FK 단계(<see cref="CopyOrder.Stages"/>), 판정(<see cref="CopyConcurrency.Effective"/>: 커밋 3 × 같은 시점 2 × 다시 넣기),
/// 사전 점검 항목(<see cref="CopyConcurrency.Notice"/>), 작업 파일 칸(기본이면 쓰지 않음·스키마 5 그대로).
/// </summary>
public sealed class CopyConcurrencyTests
{
    // ---- FK 단계 ----

    [Fact]
    public void Stages_of_a_chain_are_one_table_each_parents_first()
    {
        var stages = CopyOrder.Stages(["C", "B", "A"], [("C", "B"), ("B", "A")]);

        Assert.Equal([["A"], ["B"], ["C"]], Lists(stages));
    }

    [Fact]
    public void Stages_of_a_diamond_put_the_two_middle_tables_together()
    {
        // D → B, D → C, B → A, C → A: B·C 는 서로 잇지 않아 같은 단계다.
        var stages = CopyOrder.Stages(["D", "C", "B", "A"], [("D", "B"), ("D", "C"), ("B", "A"), ("C", "A")]);

        Assert.Equal([["A"], ["C", "B"], ["D"]], Lists(stages));
    }

    [Fact]
    public void Stage_is_one_plus_the_deepest_parent()
    {
        // X 는 A(단계 1)와 C(단계 3)의 자식이라 단계 4. Y 는 부모가 없어 단계 1.
        var stages = CopyOrder.Stages(["A", "B", "C", "X", "Y"], [("B", "A"), ("C", "B"), ("X", "A"), ("X", "C")]);

        Assert.Equal([["A", "Y"], ["B"], ["C"], ["X"]], Lists(stages));
    }

    [Fact]
    public void Unrelated_tables_share_the_first_stage_in_input_order()
    {
        var stages = CopyOrder.Stages(["B", "A", "C"], []);

        Assert.Equal([["B", "A", "C"]], Lists(stages));
    }

    [Fact]
    public void Cycle_members_and_tables_that_lean_on_them_run_alone_at_the_end_in_the_serial_order()
    {
        // A ↔ B 순환, C 는 B 의 자식(순환에 기댐), D·E 는 순환과 상관없다(E → D).
        var tables = new[] { "A", "B", "C", "D", "E" };
        var edges = new[] { ("A", "B"), ("B", "A"), ("C", "B"), ("E", "D") };
        var stages = CopyOrder.Stages(tables, edges);

        Assert.Equal([["D"], ["E"], ["A"], ["B"], ["C"]], Lists(stages));
        // 단계를 이으면 같은 테이블을 한 번씩 담는다.
        Assert.Equal(CopyOrder.Sort(tables, edges).ParentsFirst.OrderBy(t => t, StringComparer.Ordinal),
                     stages.SelectMany(s => s).OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void Self_reference_and_edges_to_tables_outside_the_job_are_ignored()
    {
        var stages = CopyOrder.Stages(["A", "B"], [("A", "A"), ("A", "OUTSIDE"), ("B", "OUTSIDE")]);

        Assert.Equal([["A", "B"]], Lists(stages));
    }

    [Fact]
    public void Parent_key_rule_edge_puts_the_child_after_the_parent()
    {
        // 부모 키 규칙 간선(사전 점검이 FK 간선에 더한다)도 같은 간선이다: FK 가 없어도 자식은 다음 단계다.
        var stages = CopyOrder.Stages(["ITEM", "ORDERS", "CODE"], [("ITEM", "ORDERS")]);

        Assert.Equal([["ORDERS", "CODE"], ["ITEM"]], Lists(stages));
    }

    // ---- 판정 ----

    [Theory]
    [InlineData(CommitMode.PerTable, true, 1, CopyConcurrency.ConsistentReadReason)]
    [InlineData(CommitMode.PerTable, false, 3, null)]
    [InlineData(CommitMode.PerBatch, true, 1, CopyConcurrency.ConsistentReadReason)]
    [InlineData(CommitMode.PerBatch, false, 3, null)]
    [InlineData(CommitMode.AtEnd, true, 1, CopyConcurrency.ConsistentReadAndAtEndReason)]
    [InlineData(CommitMode.AtEnd, false, 1, CopyConcurrency.AtEndReason)]
    public void Effective_turns_concurrency_off_for_consistent_read_and_commit_at_end(CommitMode commit, bool consistent, int expected, string? reason)
    {
        var job = Job(3, commit, consistent);

        var decision = CopyConcurrency.Effective(job, Report(["A", "B", "C"]), Dbms.Oracle);

        Assert.Equal(expected, decision.Tables);
        Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(CommitMode.PerTable, false)]
    [InlineData(CommitMode.PerBatch, true)]
    [InlineData(CommitMode.AtEnd, false)]
    public void Retrying_failed_rows_is_always_one_table_at_a_time(CommitMode commit, bool consistent)
    {
        var decision = CopyConcurrency.Effective(Job(4, commit, consistent), Report(["A", "B"]), Dbms.Oracle, retry: true);

        Assert.Equal(1, decision.Tables);
        Assert.Equal(CopyConcurrency.RetryReason, decision.Reason);
    }

    [Fact]
    public void Effective_is_capped_by_the_widest_stage_and_by_the_maximum()
    {
        var job = Job(4, CommitMode.PerTable, consistent: false);

        Assert.Equal(2, CopyConcurrency.Effective(job, Report(["A", "B"], ["C"]), Dbms.Oracle).Tables);
        Assert.Equal(new CopyConcurrencyDecision(1, CopyConcurrency.NoParallelStageReason),
                     CopyConcurrency.Effective(job, Report(["A"], ["B"]), Dbms.Oracle));
        Assert.Equal(new CopyConcurrencyDecision(1, CopyConcurrency.NoStagesReason),
                     CopyConcurrency.Effective(job, new PrecheckReport([], [], [], new Dictionary<string, long>()), Dbms.Oracle));
        Assert.Equal(4, CopyConcurrency.Effective(Job(9, CommitMode.PerTable, false), Report(["A", "B", "C", "D", "E"]), Dbms.Tibero).Tables);
        // 점검 전(화면): 작업 설정만 본다.
        Assert.Equal(new CopyConcurrencyDecision(4, null), CopyConcurrency.Effective(job, report: null, Dbms.Oracle));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 4)]
    public void Requested_is_clamped_to_one_through_the_maximum(int? value, int expected)
    {
        Assert.Equal(expected, CopyConcurrency.Requested(new CopyOptions { ConcurrentTables = value }));
    }

    [Fact]
    public void One_table_at_a_time_has_no_reason_and_no_precheck_item()
    {
        var job = Job(null, CommitMode.PerTable, consistent: true);

        Assert.Equal(new CopyConcurrencyDecision(1, null), CopyConcurrency.Effective(job, Report(["A", "B"]), Dbms.Oracle));
        Assert.Null(CopyConcurrency.Notice(job, [["A", "B"]], Dbms.Oracle));
    }

    // ---- 사전 점검 항목 ----

    [Fact]
    public void Precheck_asks_to_confirm_when_tables_really_run_together()
    {
        var item = CopyConcurrency.Notice(Job(3, CommitMode.PerTable, consistent: false), [["A", "B"], ["C"]], Dbms.Oracle);

        Assert.NotNull(item);
        Assert.Equal(CopyCheckCodes.ConcurrentCopy, item!.Code);
        Assert.Equal(CheckLevel.Confirm, item.Level);
        Assert.Null(item.Table);
        Assert.Contains("동시 복사는 지우기를 먼저 확정해서 중지해도 지운 행이 돌아오지 않습니다", item.Message, StringComparison.Ordinal);
        Assert.Contains("2개", item.Message, StringComparison.Ordinal); // 단계 안 테이블 수가 상한
        Assert.DoesNotContain("표", item.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CommitMode.PerTable, true, CopyConcurrency.ConsistentReadReason)]
    [InlineData(CommitMode.AtEnd, false, CopyConcurrency.AtEndReason)]
    public void Precheck_tells_why_it_runs_one_table_at_a_time(CommitMode commit, bool consistent, string reason)
    {
        var item = CopyConcurrency.Notice(Job(2, commit, consistent), [["A", "B"]], Dbms.Oracle);

        Assert.NotNull(item);
        Assert.Equal(CheckLevel.Pass, item!.Level);
        Assert.Contains(reason, item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Precheck_tells_when_no_stage_has_two_tables()
    {
        var item = CopyConcurrency.Notice(Job(2, CommitMode.PerTable, consistent: false), [["A"], ["B"]], Dbms.Oracle);

        Assert.Equal(CheckLevel.Pass, item!.Level);
        Assert.Contains(CopyConcurrency.NoParallelStageReason, item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_uses_one_table_at_a_time_when_the_precheck_did_not_confirm_concurrency()
    {
        // 점검 뒤 동시 수를 바꿨다(확인 항목이 없다): 사용자가 확인하지 않은 동작 변경(정리 먼저 확정)을 하지 않는다.
        var job = Job(3, CommitMode.PerTable, consistent: false);
        var unconfirmed = Report(["A", "B"]);
        var confirmed = unconfirmed with { Items = [CopyConcurrency.Notice(job, unconfirmed.Stages!, Dbms.Oracle)!] };

        Assert.Equal(1, CopyConcurrency.ForRun(job, unconfirmed, Dbms.Oracle).Tables);
        Assert.Equal(2, CopyConcurrency.ForRun(job, confirmed, Dbms.Oracle).Tables);
    }

    // ---- 작업 파일 ----

    [Fact]
    public void Concurrent_tables_round_trips_and_the_default_is_not_written_and_the_schema_stays()
    {
        var job = Job(3, CommitMode.PerTable, consistent: false);
        var json = JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(3, (int)node["options"]!["concurrentTables"]!);
        Assert.Equal(5, (int)node["schemaVersion"]!); // 스키마 버전은 올리지 않는다(결정 77 과 같은 규칙)
        Assert.Equal(CopyJobSchema.Current, (int)node["schemaVersion"]!);
        Assert.Equal(3, JsonSerializer.Deserialize(json, CopyJobJsonContext.Default.CopyJob)!.Options.ConcurrentTables);

        var plain = JsonSerializer.Serialize(Job(null, CommitMode.PerTable, consistent: true), CopyJobJsonContext.Default.CopyJob);
        Assert.DoesNotContain("concurrentTables", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_the_key_reads_as_one_table_at_a_time()
    {
        // 예전 앱이 저장한 파일(칸 없음) = 기본 1. 예전 앱은 이 칸을 모르는 키로 건너뛴다(CopyJobJsonTests 의 모르는 키 시험).
        var node = JsonNode.Parse(JsonSerializer.Serialize(Job(3, CommitMode.PerTable, consistent: false), CopyJobJsonContext.Default.CopyJob))!;
        node["options"]!.AsObject().Remove("concurrentTables");

        var back = JsonSerializer.Deserialize(node.ToJsonString(), CopyJobJsonContext.Default.CopyJob)!;

        Assert.Null(back.Options.ConcurrentTables);
        Assert.Equal(1, CopyConcurrency.Requested(back.Options));
    }

    // ---- 도우미 ----

    private static List<List<string>> Lists(IReadOnlyList<IReadOnlyList<string>> stages) => [.. stages.Select(s => s.ToList())];

    private static CopyJob Job(int? concurrent, CommitMode commit, bool consistent) => new()
    {
        Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
        SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", ConsistentRead = consistent,
        Options = new CopyOptions { Commit = commit, ConcurrentTables = concurrent },
    };

    private static PrecheckReport Report(params string[][] stages) =>
        new([], [], [], new Dictionary<string, long>()) { Stages = [.. stages.Select(s => (IReadOnlyList<string>)s)] };
}
