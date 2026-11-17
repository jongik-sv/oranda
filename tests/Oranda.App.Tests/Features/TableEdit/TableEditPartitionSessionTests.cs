using Oranda.App.Features.Localization;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지 편집(설계 18.4, 시안 ⑥)을 실제 Core 엔진으로: 안 고치면 0문장, ＋·－·나누기·합치기·비우기·경계 채우기·자동 추가가 그 문장 하나씩,
/// 원본 경계는 못 고침, 문제는 이유와 함께 문장 없음, 일반 → 파티션 바꾸기, 못 고치는 테이블은 모두 흐림.
/// </summary>
public class TableEditPartitionSessionTests
{
    internal static readonly ServerInfo Oracle19 =
        new("19.0.0.0.0", null, null, null, null, null, false, 1, new ServerCapabilities(false, false, true, false, false, Dbms.Oracle), Dbms: Dbms.Oracle);

    /// <summary>SAMPLE.ORDERS + ORDER_DATE(DATE), 월 범위 파티션 셋(P_2024_01 · P_2026_09 · P_2026_10).</summary>
    internal static TableInfo PartitionedOrders(string method = "RANGE", string lastBound = "DATE '2026-11-01'", string? interval = null)
    {
        var hash = method == "HASH";
        return WithOrderDate(OrdersTable()) with
        {
            IsPartitioned = true,
            Partitioning = new PartitioningInfo(method, ["ORDER_DATE"],
            [
                new PartitionInfo("P_2024_01", 1, hash ? null : "DATE '2024-02-01'") { NumRows = 95020 },
                new PartitionInfo("P_2026_09", 2, hash ? null : "DATE '2026-10-01'") { NumRows = 182400 },
                new PartitionInfo("P_2026_10", 3, hash ? null : lastBound) { NumRows = 190115 },
            ])
            {
                Interval = interval,
            },
        };
    }

    internal static TableInfo WithOrderDate(TableInfo table) =>
        table with { Columns = [.. table.Columns, new ColumnInfo("ORDER_DATE", table.Columns.Count + 1, "DATE")] };

    internal static TableEditSession Create(TableInfo table, ITableEditDialect? dialect = null, ServerInfo? server = null)
    {
        var d = dialect ?? new OracleRules();
        var engine = CoreTableEditEngine.Instance;
        var session = new TableEditSession(engine.FromTable(table, d), d, new TableEditContext(128, ConnectionEnvironment.Development, server ?? Oracle19), engine);
        session.SetPartitions(table);
        return session;
    }

    private static List<string> Texts(TableEditSession s) => [.. s.Script.Statements.Select(x => x.Statement.Text)];

    private static PartitionRowViewModel Select(TableEditSession s, string name)
    {
        var row = s.PartitionRows.Single(r => r.Name == name);
        s.SelectedPartition = row;
        return row;
    }

    [Fact]
    public void Untouched_partitioned_table_has_no_statements_and_passing_through_cells_changes_nothing()
    {
        var s = Create(PartitionedOrders());

        Assert.Equal(["P_2024_01", "P_2026_09", "P_2026_10"], s.PartitionRows.Select(r => r.Name));
        Assert.Equal("182,400", s.PartitionRows[1].RowCountText);
        Assert.Equal("RANGE", s.PartitionMethodText);
        Assert.Equal("ORDER_DATE", s.PartitionKeyText);
        Assert.False(s.CanEditPartitionKeys);
        foreach (var row in s.PartitionRows)
        {
            row.Name = row.Name;
            row.BoundText = row.BoundText;
            row.Tablespace = row.Tablespace;
        }

        s.PartitionIntervalText = s.PartitionIntervalText;

        Assert.False(s.IsDirty);
        Assert.False(s.IsPartitionDirty);
        Assert.True(s.Script.IsEmpty);
        Assert.Empty(s.PartitionWarnings);
        Assert.Empty(s.PartitionProblemLines);
        Assert.All(s.PartitionRows, r => Assert.Equal(string.Empty, r.StateMark));
    }

    [Fact]
    public void Add_appends_after_the_last_row_and_becomes_one_add_partition_statement()
    {
        var s = Create(PartitionedOrders());

        var row = s.AddPartition()!;
        Assert.Same(row, s.PartitionRows[^1]);
        Assert.Same(row, s.SelectedPartition);
        Assert.True(row.IsAdded);
        Assert.True(row.CanEditBound);
        // 앞 간격(한 달)을 이어 이름·경계를 채운다(쉬운 편집). 경계는 보통 값으로 보인다.
        Assert.Equal("P_2026_11", row.Row.Name);
        Assert.Equal("2026-12-01", row.BoundText);
        Assert.False(row.HasError);

        row.Name = "p_2026_11";
        row.BoundText = "DATE '2026-12-01'";

        Assert.False(row.HasError);
        var text = Assert.Single(Texts(s));
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_11 VALUES LESS THAN (DATE '2026-12-01')", text);
        Assert.Equal(text + ";", s.PreviewText);
        Assert.Equal(1, s.CountFor(TableEditPage.Partitions));
        Assert.Empty(s.PartitionProblemLines);
    }

    [Fact]
    public void Drop_toggle_warns_with_the_row_count_and_toggling_again_restores()
    {
        var s = Create(PartitionedOrders());
        var row = Select(s, "P_2024_01");

        s.DropSelectedPartition();

        Assert.True(row.IsDropped);
        Assert.Equal("−", row.StateMark);
        Assert.False(row.CanEditName);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS DROP PARTITION P_2024_01 UPDATE INDEXES"], Texts(s));
        Assert.Equal(["⚠ 파티션 지우기 P_2024_01: 95,020행이 지워지고 되돌릴 수 없음"], s.PartitionWarnings);

        s.DropSelectedPartition();

        Assert.False(row.IsDropped);
        Assert.True(s.Script.IsEmpty);
        Assert.Empty(s.PartitionWarnings);
        Assert.False(s.IsDirty);
        Assert.Same(row, s.SelectedPartition);
    }

    [Fact]
    public void Drop_of_a_new_row_removes_it()
    {
        var s = Create(PartitionedOrders());
        s.AddPartition();
        Assert.Equal(4, s.PartitionRows.Count);

        s.DropSelectedPartition();

        Assert.Equal(3, s.PartitionRows.Count);
        Assert.Null(s.SelectedPartition);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Split_inserts_a_new_row_before_the_selected_one_and_yields_split()
    {
        var s = Create(PartitionedOrders());
        var source = Select(s, "P_2026_10");

        var part = s.SplitSelectedPartition()!;

        Assert.Equal(2, s.PartitionRows.IndexOf(part));
        Assert.Same(source, s.PartitionRows[3]);
        Assert.Equal(source.Key, part.Row.SplitFrom);
        Assert.Same(part, s.SelectedPartition);
        part.Name = "P_2026_10_A";
        part.BoundText = "DATE '2026-10-16'";

        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SPLIT PARTITION P_2026_10 AT (DATE '2026-10-16') INTO (PARTITION P_2026_10_A, PARTITION P_2026_10) UPDATE INDEXES"], Texts(s));
        Assert.True(part.IsAdded);
        Assert.Contains("⑂", part.StateMark, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_joins_the_selected_row_into_the_next_and_toggles_off()
    {
        var s = Create(PartitionedOrders());
        Select(s, "P_2026_10");
        Assert.False(s.CanMergePartition); // 마지막 줄은 다음 줄이 없다
        var row = Select(s, "P_2026_09");
        Assert.True(s.CanMergePartition);

        s.MergeSelectedPartition();

        Assert.Equal(s.PartitionRows[2].Key, row.Row.MergedInto);
        Assert.True(row.IsMergedAway);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS MERGE PARTITIONS P_2026_09, P_2026_10 INTO PARTITION P_2026_10 UPDATE INDEXES"], Texts(s));
        Assert.Contains(s.PartitionWarnings, w => w.Contains("P_2026_09", StringComparison.Ordinal) && w.Contains("182,400", StringComparison.Ordinal));

        s.MergeSelectedPartition();

        Assert.Null(row.Row.MergedInto);
        Assert.True(s.Script.IsEmpty);
    }

    [Fact]
    public void Truncate_toggle_yields_truncate_and_warns()
    {
        var s = Create(PartitionedOrders());
        var row = Select(s, "P_2024_01");

        s.TruncateSelectedPartition();

        Assert.True(row.IsTruncated);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS TRUNCATE PARTITION P_2024_01 UPDATE INDEXES"], Texts(s));
        Assert.Equal(["⚠ 파티션 비우기 P_2024_01: 95,020행이 지워지고 되돌릴 수 없음"], s.PartitionWarnings);

        s.RevertSelectedPartition();

        Assert.False(row.IsTruncated);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Rename_of_an_existing_row_is_allowed_but_its_bound_and_tablespace_are_not()
    {
        var s = Create(PartitionedOrders());
        var row = s.PartitionRows[1];

        Assert.False(row.CanEditBound);
        Assert.False(row.CanEditTablespace);
        row.BoundText = "DATE '2026-12-01'";
        row.Tablespace = "TS_ARCHIVE";

        Assert.Equal("DATE '2026-10-01'", row.Row.Bound);
        Assert.True(s.Script.IsEmpty);
        Assert.False(s.IsDirty);

        row.Name = "P_SEP";
        Assert.True(row.IsNameChanged);
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS RENAME PARTITION P_2026_09 TO P_SEP"], Texts(s));
    }

    [Fact]
    public void Bound_fill_appends_monthly_rows_and_skips_names_that_exist()
    {
        var s = Create(PartitionedOrders());
        s.IsBoundFillOpen = true;
        s.BoundFillStart = "2026-10";
        s.BoundFillUnitIndex = 1; // 월
        s.BoundFillCount = "4";
        s.BoundFillStep = "1";

        Assert.True(s.ApplyBoundFill());

        Assert.Equal(["P_2024_01", "P_2026_09", "P_2026_10", "P_2026_11", "P_2026_12", "P_2027_01"], s.PartitionRows.Select(r => r.Name));
        Assert.Equal(
        [
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_11 VALUES LESS THAN (DATE '2026-12-01')",
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2026_12 VALUES LESS THAN (DATE '2027-01-01')",
            "ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_2027_01 VALUES LESS THAN (DATE '2027-02-01')",
        ], Texts(s));
        Assert.False(s.IsBoundFillOpen);
        Assert.Null(s.BoundFillError);
    }

    [Fact]
    public void Bound_fill_rejects_bad_input_without_changing_the_model()
    {
        var s = Create(PartitionedOrders());
        s.BoundFillStart = "다음 달";
        s.BoundFillUnitIndex = 1;
        s.BoundFillCount = "3";

        Assert.False(s.ApplyBoundFill());
        Assert.NotNull(s.BoundFillError);

        s.BoundFillStart = "2027-01";
        s.BoundFillCount = "0";
        Assert.False(s.ApplyBoundFill());
        Assert.NotNull(s.BoundFillError);
        Assert.False(s.IsDirty);

        s.BoundFillUnitIndex = 3; // 숫자
        s.BoundFillStart = "1000";
        s.BoundFillCount = "2";
        s.BoundFillStep = "500";
        Assert.True(s.ApplyBoundFill());
        Assert.Equal(["P_1000", "P_1500"], s.PartitionRows.Skip(3).Select(r => r.Name));
        Assert.Equal(["1500", "2000"], s.PartitionRows.Skip(3).Select(r => r.BoundText));
    }

    [Fact]
    public void Add_after_maxvalue_shows_the_reason_and_produces_no_statement()
    {
        var s = Create(PartitionedOrders(lastBound: "MAXVALUE"));

        var row = s.AddPartition()!;
        row.Name = "P_LATE";
        row.BoundText = "DATE '2027-01-01'";

        Assert.True(s.Script.IsEmpty);
        Assert.True(s.IsDirty);
        var reason = UiText.Get(PartitionDiff.AddAfterMaxValueKey, PartitionDiff.AddAfterMaxValueKey);
        Assert.True(row.HasProblem);
        Assert.Contains("⚠", row.StateMark, StringComparison.Ordinal);
        Assert.Contains(reason, row.Tip, StringComparison.Ordinal);
        Assert.Contains(s.PartitionProblemLines, l => l.Contains("P_LATE", StringComparison.Ordinal) && l.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void Interval_cell_turns_automatic_add_on_and_off()
    {
        var s = Create(PartitionedOrders());
        Assert.True(s.CanEditPartitionInterval);

        s.PartitionIntervalText = "NUMTOYMINTERVAL(1, 'MONTH')";
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))"], Texts(s));

        s.PartitionIntervalText = string.Empty;
        Assert.True(s.Script.IsEmpty);

        var on = Create(PartitionedOrders(interval: "NUMTOYMINTERVAL(1, 'MONTH')"));
        on.PartitionIntervalText = " ";
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS SET INTERVAL ()"], Texts(on));
    }

    [Fact]
    public void Hash_tables_disable_split_merge_fill_and_bounds()
    {
        var s = Create(PartitionedOrders("HASH"));
        Select(s, "P_2024_01");

        Assert.Equal("HASH", s.PartitionMethodText);
        Assert.Equal("—", s.BoundHeaderText);
        Assert.True(s.CanAddPartition);
        Assert.True(s.CanDropPartition);
        Assert.False(s.CanSplitPartition);
        Assert.False(s.CanMergePartition);
        Assert.False(s.CanFillBounds);
        Assert.False(s.CanEditPartitionInterval);
        var row = s.AddPartition()!;
        Assert.False(row.CanEditBound);
        Assert.False(row.HasError); // 해시는 경계가 없다
        Assert.Equal(["ALTER TABLE SAMPLE.ORDERS ADD PARTITION P_NEW1 UPDATE INDEXES"], Texts(s));
    }

    [Fact]
    public void List_tables_name_the_bound_column_values()
    {
        var s = Create(PartitionedOrders("LIST", "'Z'"));
        Assert.Equal("값 목록", s.BoundHeaderText);
        Assert.False(s.CanFillBounds);
        Assert.False(s.CanEditPartitionInterval);
    }

    [Fact]
    public void Non_partitioned_table_converts_with_one_partition_by_statement()
    {
        var s = Create(WithOrderDate(OrdersTable()));

        Assert.Empty(s.PartitionRows);
        Assert.True(s.CanChoosePartitionMethod);
        Assert.Equal(0, s.PartitionMethodIndex);
        Assert.False(s.CanAddPartition);

        s.PartitionMethodIndex = 1; // RANGE
        Assert.True(s.CanEditPartitionKeys);
        s.PartitionKeyText = "nope";
        Assert.NotNull(s.PartitionHeaderError);
        Assert.Empty(s.PartitionDraft!.Scheme!.KeyColumns);

        s.PartitionKeyText = "order_date";
        Assert.Null(s.PartitionHeaderError);
        var row = s.AddPartition()!;
        row.Name = "P_OLD";
        row.BoundText = "DATE '2026-01-01'";
        var max = s.AddPartition()!;
        max.Name = "P_MAX";
        max.BoundText = "MAXVALUE";

        var text = Assert.Single(Texts(s));
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS MODIFY PARTITION BY RANGE (ORDER_DATE) (PARTITION P_OLD VALUES LESS THAN (DATE '2026-01-01'), "
                     + "PARTITION P_MAX VALUES LESS THAN (MAXVALUE)) ONLINE", text);

        s.PartitionMethodIndex = 0; // 없음 = 되돌림
        Assert.Empty(s.PartitionRows);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Conversion_the_dialect_cannot_do_disables_the_method_choice_with_the_reason()
    {
        const string key = "TableEdit.unsupported.tibero.partitionConversion";
        var s = Create(WithOrderDate(OrdersTable()), new OracleRules { Unsupported = f => f == TableEditFeature.PartitionConversion ? key : null });

        Assert.True(s.CanEditPartitions);
        Assert.False(s.CanChoosePartitionMethod);
        Assert.Equal(UiText.Get(key, key), s.PartitionMethodTip);
        s.PartitionMethodIndex = 1;
        Assert.Null(s.PartitionDraft!.Scheme);
    }

    [Fact]
    public void Restricted_or_unsupported_tables_lock_every_partition_action()
    {
        var restricted = Create(PartitionedOrders() with { Kind = TableKind.Temporary });
        var unsupported = Create(PartitionedOrders(), new OracleRules { Unsupported = f => f == TableEditFeature.Partitioning ? "TableEdit.unsupported.x" : null });

        foreach (var s in new[] { restricted, unsupported })
        {
            s.SelectedPartition = s.PartitionRows[0];
            Assert.False(s.CanEditPartitions);
            Assert.NotEqual(string.Empty, s.PartitionLockedReason);
            Assert.False(s.CanAddPartition);
            Assert.False(s.CanDropPartition);
            Assert.False(s.CanSplitPartition);
            Assert.False(s.CanMergePartition);
            Assert.False(s.CanTruncatePartition);
            Assert.False(s.CanRevertPartition);
            Assert.False(s.CanFillBounds);
            Assert.False(s.CanChoosePartitionMethod);
            Assert.False(s.CanEditPartitionInterval);
            Assert.False(s.PartitionRows[0].CanEditName);
            Assert.Null(s.AddPartition());
            s.DropSelectedPartition();
            s.PartitionRows[0].Name = "P_X";
            Assert.False(s.IsDirty);
        }
    }

    [Fact]
    public void Row_view_models_stay_the_same_instances_across_edits()
    {
        var s = Create(PartitionedOrders());
        var before = s.PartitionRows.ToList();

        s.AddPartition();
        Select(s, "P_2026_09");
        s.TruncateSelectedPartition();

        Assert.Equal(before, s.PartitionRows.Take(3));
        s.RevertPartitions();
        Assert.Equal(before, s.PartitionRows);
        Assert.Same(before[1], s.SelectedPartition);
    }

    /// <summary>Core 기본 규칙(Oracle)만 쓰는 방언. <see cref="Unsupported"/> 로 기능 하나를 막을 수 있다.</summary>
    internal sealed class OracleRules : ITableEditDialect
    {
        public Func<TableEditFeature, string?> Unsupported { get; init; } = _ => null;

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "DATE"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => Unsupported(feature);

        /// <summary>파티션 문장 하나를 막는다(기본 = 모두 됨).</summary>
        public Func<Oranda.Core.Schema.TableEdit.Partitions.PartitionOperation, string?> UnsupportedPartition { get; init; } = _ => null;

        public string? UnsupportedPartitionReasonKey(Oranda.Core.Schema.TableEdit.Partitions.PartitionOperation operation, Oranda.Core.Schema.TableEdit.Partitions.PartitionMethod method, ServerInfo? server) =>
            UnsupportedPartition(operation);
    }
}
