using Avalonia.Controls;
using Avalonia.Interactivity;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 파티션 페이지의 하위 파티션 칸(PA-11, 바꾸기 중일 때만)·LOCAL 인덱스 검사(PA-12)·「월 추가 스크립트 저장…」(PA-5). 예시 = SAMPLE.ORDERS.
/// </summary>
public class TableEditPartitionSubTests
{
    private static readonly DateTime Today = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Unspecified);

    private static TableEditSession Converting()
    {
        var s = Create(WithOrderDate(OrdersTable()));
        s.Today = () => Today;
        Assert.True(s.ApplyEasyPartitioning());
        return s;
    }

    /// <summary>SAMPLE.ORDERS + 문자열 YM(YYYYMMDD) 범위 파티션, 마지막 MAXVALUE, INTERVAL 없음.</summary>
    private static TableInfo StringMonthly(string lastBound = "MAXVALUE") => OrdersTable() with
    {
        Columns = [.. OrdersTable().Columns, new ColumnInfo("YM", 5, "VARCHAR2") { Length = 8 }],
        IsPartitioned = true,
        Partitioning = new PartitioningInfo("RANGE", ["YM"],
        [
            new PartitionInfo("P_202610", 1, "'20261101'"),
            new PartitionInfo("P_202611", 2, "'20261201'"),
            new PartitionInfo("P_MAX", 3, lastBound),
        ]),
    };

    private static string Statement(TableEditSession s) => Assert.Single(s.Script.Statements).Statement.Text;

    [Fact]
    public void List_template_rows_become_the_subpartition_clause_and_values_are_typed_plainly()
    {
        var s = Converting();
        Assert.True(s.ShowsSubpartitionEditor);
        Assert.False(s.HasSubpartition);

        s.SubpartitionMethodIndex = 1;
        s.SubpartitionKeyText = "STATUS";
        Assert.Equal(["SP_OTHER"], s.SubpartitionRows.Select(r => r.Name)); // 시작 = 나머지 전부 한 줄
        Assert.Equal("DEFAULT", s.SubpartitionRows[0].ValuesText);

        var added = s.AddSubpartitionRow()!;
        Assert.Equal(0, added.Index); // DEFAULT 줄 앞에
        added.Name = "SP_A";
        s.SubpartitionRows[0].ValuesText = "A, B";

        Assert.Contains(" SUBPARTITION BY LIST (STATUS) SUBPARTITION TEMPLATE (SUBPARTITION SP_A VALUES ('A', 'B'), SUBPARTITION SP_OTHER VALUES (DEFAULT)) (PARTITION",
            Statement(s), StringComparison.Ordinal);
        Assert.DoesNotContain(s.PartitionFindings(), f => f.Severity == CheckSeverity.Block);

        s.SelectedSubpartitionRow = s.SubpartitionRows[0];
        s.RemoveSelectedSubpartitionRow();
        Assert.Equal(["SP_OTHER"], s.SubpartitionRows.Select(r => r.Name));
    }

    [Fact]
    public void Hash_count_and_back_to_none_restores_the_plain_conversion()
    {
        var s = Converting();
        var plain = Statement(s);

        s.SubpartitionMethodIndex = 2;
        s.SubpartitionKeyText = "CUSTOMER_ID";
        Assert.Equal("4", s.SubpartitionHashCountText);
        s.SubpartitionHashCountText = "8";
        Assert.Contains(" SUBPARTITION BY HASH (CUSTOMER_ID) SUBPARTITIONS 8 (PARTITION", Statement(s), StringComparison.Ordinal);

        s.SubpartitionHashCountText = "0"; // 틀린 수는 모델을 바꾸지 않는다
        Assert.NotNull(s.PartitionHeaderError);
        Assert.Contains("SUBPARTITIONS 8", Statement(s), StringComparison.Ordinal);

        s.SubpartitionMethodIndex = 0;
        Assert.Equal(plain, Statement(s));
    }

    [Fact]
    public void Missing_subpartition_keys_unknown_columns_and_local_indexes_are_blocked()
    {
        var s = Converting();
        s.SubpartitionMethodIndex = 1;
        Assert.Contains(s.PartitionFindings(), f => f.Severity == CheckSeverity.Block && f.Detail!.Contains("하위 파티션 키", StringComparison.Ordinal));

        s.SubpartitionKeyText = "NO_SUCH";
        Assert.Contains("NO_SUCH", s.PartitionHeaderError, StringComparison.Ordinal);
        Assert.Empty(s.PartitionDraft!.Scheme!.SubpartitionTemplate!.KeyColumns);

        s.SubpartitionKeyText = "STATUS";
        s.UpdatePartitions(d => d with { LocalIndexes = ["IX_ORDERS_01", "IX_GONE"] });
        var block = Assert.Single(s.PartitionFindings(), f => f.Severity == CheckSeverity.Block);
        Assert.Equal("IX_GONE", block.Subject);
        Assert.EndsWith("UPDATE INDEXES (IX_ORDERS_01 LOCAL, IX_GONE LOCAL, PK_ORDERS GLOBAL)", Statement(s), StringComparison.Ordinal);
    }

    /// <summary>이미 파티션된 테이블은 하위 칸이 없고 보기 글만(TE-42).</summary>
    [Fact]
    public void Partitioned_tables_show_subpartitions_as_text_only()
    {
        var s = Create(PartitionedOrders());
        Assert.False(s.ShowsSubpartitionEditor);
        s.SubpartitionMethodIndex = 1;
        Assert.Null(s.PartitionDraft!.Scheme!.SubpartitionTemplate);
        Assert.Empty(s.Script.Statements);
    }

    [Fact]
    public async Task Month_add_script_shows_only_for_a_maxvalue_range_without_interval()
    {
        var s = Create(StringMonthly());
        var saved = 0;
        Assert.False(s.ShowsMonthAddScript); // 저장 길이 없으면 숨김
        s.SaveMonthAddScript = () =>
        {
            saved++;
            return Task.CompletedTask;
        };
        Assert.True(s.ShowsMonthAddScript);
        Assert.Contains("P_202612 ~ P_202711 (12개월)", s.MonthAddScriptTip, StringComparison.Ordinal);
        var script = s.BuildMonthAddScript()!;
        Assert.Contains("SPLIT PARTITION P_MAX AT ('20270101') INTO (PARTITION P_202612, PARTITION P_MAX) UPDATE INDEXES;", script, StringComparison.Ordinal);
        await s.SaveMonthAddScriptAsync();
        Assert.Equal(1, saved);

        var noMax = Create(StringMonthly("'20270101'"));
        noMax.SaveMonthAddScript = () => Task.CompletedTask;
        Assert.False(noMax.ShowsMonthAddScript);
        var withInterval = Create(PartitionedOrders(interval: "NUMTOYMINTERVAL(1, 'MONTH')"));
        withInterval.SaveMonthAddScript = () => Task.CompletedTask;
        Assert.False(withInterval.ShowsMonthAddScript);
        Assert.Null(withInterval.BuildMonthAddScript());
    }

    /// <summary>객체 상세의 저장: 기존 스크립트 저장 방식(파일 고르기 → UTF-8 글), 이름 = 표_add_months.sql.</summary>
    [Fact]
    public async Task Object_detail_saves_the_month_add_script_to_the_picked_file()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = StringMonthly();
        using var dir = new Oranda.Testing.TempDirectory("oranda-te-month-");
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "months.sql");
        string? suggested = null;
        vm.PickScriptFile = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(path);
        };
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        Assert.True(vm.TableEdit!.ShowsMonthAddScript);

        Assert.Equal(path, await vm.SaveMonthAddScriptAsync());

        Assert.Equal("ORDERS_add_months.sql", suggested);
        Assert.Equal(vm.TableEdit.BuildMonthAddScript(), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public Task Subpartition_panel_appears_while_converting_and_list_rows_render() => HeadlessHost.Run(() =>
    {
        var s = Converting();
        var view = new TableEditPartitionsView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Control>("SubpartitionPanel")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<TextBlock>("SubpartitionText")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("SubpartitionRowList")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Button>("MonthAddScriptButton")!.IsEffectivelyVisible);

            view.FindControl<ComboBox>("SubpartitionMethodChoice")!.SelectedIndex = 1;
            HeadlessHost.Settle(w);
            Assert.True(s.IsListSubpartition);
            Assert.True(view.FindControl<Control>("SubpartitionRowList")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("SubpartitionCountBox")!.IsEffectivelyVisible);
            view.FindControl<Button>("AddSubpartitionRowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessHost.Settle(w);
            Assert.Equal(2, view.FindControl<ListBox>("SubpartitionRowList")!.ItemCount);

            view.FindControl<ComboBox>("SubpartitionMethodChoice")!.SelectedIndex = 2;
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Control>("SubpartitionCountBox")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("SubpartitionRowList")!.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Month_add_button_shows_on_a_string_monthly_table_and_hides_the_editor() => HeadlessHost.Run(() =>
    {
        var s = Create(StringMonthly());
        s.SaveMonthAddScript = () => Task.CompletedTask;
        var view = new TableEditPartitionsView { DataContext = s };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            HeadlessHost.Settle(w);
            Assert.True(view.FindControl<Button>("MonthAddScriptButton")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Control>("SubpartitionPanel")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<TextBlock>("SubpartitionText")!.IsEffectivelyVisible);
        }
        finally
        {
            w.Close();
        }
    });
}
