using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>T15 리뷰에서 나온 경우: 파티션 문제 ✖, 일반 → 파티션 바꾸기 검증, 방언 미지원 파티션 동작 흐림, 전역 인덱스 다시 만들기 목록, 식 나누기, 종류 바꾸기, 빈 이름.</summary>
public class TableEditPhase2ReviewFixTests
{
    private static TableInfo Partitioned(string method = "RANGE") => OrdersTable() with
    {
        Columns = [.. OrdersTable().Columns, new ColumnInfo("ORDER_DATE", 5, "DATE") { IsNullable = false }],
        IsPartitioned = true,
        Partitioning = new PartitioningInfo(method, ["ORDER_DATE"],
        [
            new PartitionInfo("P1", 1, method == "HASH" ? null : "DATE '2024-02-01'") { NumRows = 10 },
            new PartitionInfo("P2", 2, method == "HASH" ? null : "DATE '2026-11-01'") { NumRows = 20 },
        ]),
    };

    private static TableEditSession Create(TableInfo table, Rules? dialect = null)
    {
        dialect ??= new Rules();
        var engine = CoreTableEditEngine.Instance;
        var server = new ServerInfo("19.0.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, false, false, false));
        var edit = new TableEditSession(engine.FromTable(table, dialect), dialect, new TableEditContext(128, ConnectionEnvironment.Development, server), engine);
        edit.SetPartitions(table);
        return edit;
    }

    [Fact]
    public void Partition_edits_the_core_cannot_write_block_the_run_instead_of_being_lost()
    {
        var edit = Create(Partitioned());
        edit.UpdatePartitions(d => d with { Partitions = [.. d.Partitions.Select(p => p.Name == "P1" ? p with { Bound = "DATE '2024-03-01'" } : p)] }); // 원본 경계 바꾸기 = 문장 없음

        Assert.NotEmpty(edit.PartitionChanges.Problems);
        var block = Assert.Single(edit.PartitionFindings(), f => f.Code == CheckCode.Unsupported);
        Assert.Equal(CheckSeverity.Block, block.Severity);
        Assert.Equal(PartitionDiff.BoundChangeKey, block.Detail);
        Assert.True(edit.IsDirty);
    }

    [Fact]
    public void Converting_to_partitioned_needs_keys_that_exist_and_at_least_one_partition()
    {
        var edit = Create(OrdersTable());
        Assert.Empty(edit.PartitionFindings());

        edit.UpdatePartitions(d => d with { Scheme = new PartitionScheme(PartitionMethod.Range, []) });
        var details = edit.PartitionFindings().Where(f => f.Severity == CheckSeverity.Block).Select(f => f.Detail).ToList();
        Assert.Contains(details, d => d!.Contains("키 열을 고르세요", StringComparison.Ordinal));
        Assert.Contains(details, d => d!.Contains("하나 이상", StringComparison.Ordinal));

        edit.UpdatePartitions(d => d with
        {
            Scheme = new PartitionScheme(PartitionMethod.Range, ["STATUS"]),
            Partitions = [new PartitionDraft(DraftKeys.New(), "P_A") { Bound = "'M'" }],
        });
        Assert.DoesNotContain(edit.PartitionFindings(), f => f.Severity == CheckSeverity.Block);

        edit.Columns.Single(c => c.Name == "STATUS").Name = "STATE"; // 키로 고른 열 이름을 바꿈 → MODIFY … (STATUS) 가 실패할 꼴
        Assert.Contains(edit.PartitionFindings(), f => f.Severity == CheckSeverity.Block && f.Subject == "STATUS");
    }

    [Fact]
    public void Partition_operations_the_dialect_refuses_are_disabled_with_the_reason()
    {
        var dialect = new Rules();
        dialect.RefusedPartitions.Add(PartitionOperation.Add);
        var edit = Create(Partitioned("HASH"), dialect);

        Assert.False(edit.CanAddPartition);
        Assert.Contains("TableEdit.unsupported.test", edit.PartitionUnsupportedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Global_index_rebuilds_follow_the_edited_indexes()
    {
        var dialect = new Rules { KeepsGlobals = false };
        var edit = Create(Partitioned(), dialect);
        edit.ToggleDrop(edit.Indexes.Single(i => i.Name == "IX_ORDERS_01").Key); // 지운 전역 인덱스는 다시 만들지 않는다
        var ix = edit.AddIndex()!;
        ix.ColumnsText = "STATUS";
        edit.UpdatePartitions(d => d with
        {
            Partitions = [new PartitionDraft(DraftKeys.New(), "P0") { Bound = "DATE '2023-01-01'", SplitFrom = "p1" }, .. d.Partitions],
        });

        var sql = string.Join("\n", edit.Script.Statements.Select(s => s.Statement.Text));
        Assert.Contains("SPLIT PARTITION", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IX_ORDERS_01 REBUILD", sql, StringComparison.Ordinal);
        Assert.Contains(ix.Index.Name + " REBUILD", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NVL(A, B)", new[] { "NVL(A, B)" })]
    [InlineData("REPLACE(NAME, ',', ''), ID DESC", new[] { "REPLACE(NAME, ',', '')", "ID DESC" })]
    [InlineData("SUBSTR(\"NAME\",1,3)", new[] { "SUBSTR(\"NAME\",1,3)" })]
    [InlineData("A, \"B,C\"", new[] { "A", "\"B,C\"" })]
    public void Lists_split_only_on_top_level_commas(string text, string[] expected)
    {
        Assert.Equal(expected, TableEditCellText.SplitList(text));
    }

    [Fact]
    public void Switching_a_function_based_index_with_expressions_to_normal_is_refused()
    {
        var edit = Create(OrdersTable());
        var ix = edit.AddIndex()!;
        ix.KindIndex = 1;
        ix.ColumnsText = "UPPER(STATUS)";

        ix.KindIndex = 0;

        Assert.True(ix.HasError);
        Assert.Equal(IndexKind.FunctionBased, ix.Index.Kind);
        Assert.Equal("UPPER(STATUS)", ix.Index.Columns[0].Expression);
    }

    [Fact]
    public void An_empty_table_name_is_refused_and_the_cell_shows_the_name_again()
    {
        var edit = Create(OrdersTable());
        var raised = new List<string?>();
        edit.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        edit.TableName = "  ";

        Assert.Equal("ORDERS", edit.TableName);
        Assert.Contains(nameof(TableEditSession.TableName), raised);
        Assert.False(edit.IsDirty);
    }

    private sealed class Rules : ITableEditDialect
    {
        public HashSet<PartitionOperation> RefusedPartitions { get; } = [];

        public bool KeepsGlobals { get; init; } = true;

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public bool KeepsGlobalIndexesOnSplitMerge => KeepsGlobals;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;

        public string? UnsupportedPartitionReasonKey(PartitionOperation operation, PartitionMethod method, ServerInfo? server) =>
            RefusedPartitions.Contains(operation) ? "TableEdit.unsupported.test" : null;
    }
}
