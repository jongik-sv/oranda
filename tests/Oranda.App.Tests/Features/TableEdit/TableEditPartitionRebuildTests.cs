using Avalonia.Controls;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema;
using Oranda.Core.Schema.TableEdit;
using Oranda.App.Tests.Shell.Headless;
using static Oranda.App.Tests.Features.TableEdit.TableEditPartitionSessionTests;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 일반 → 파티션 바꾸기가 한 문장으로 안 되는 서버(Oracle 12.2 미만·Tibero, 설계 TE-41): 「실행」 대신 「다시 만들기 스크립트」 줄,
/// 서버가 아예 못 바꾸면 「파티션 테이블로 바꾸기…」를 숨기고 이유를 글로 보인다(2026-11-18 Tibero 6 에서 막다른 페이지였던 것).
/// </summary>
public class TableEditPartitionRebuildTests
{
    private static readonly ServerInfo Oracle11 = Oracle19 with { Version = "11.2.0.4.0" };

    private static readonly DateTime Today = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Unspecified);

    private static TableEditSession Old(OracleRules? rules = null)
    {
        var s = Create(WithOrderDate(OrdersTable()), rules, Oracle11);
        s.Today = () => Today;
        return s;
    }

    [Fact]
    public async Task Easy_setup_on_an_old_server_offers_the_rebuild_script_and_runs_nothing()
    {
        var s = Old();
        var opened = 0;
        s.OpenPartitionRebuild = () =>
        {
            opened++;
            return Task.CompletedTask;
        };
        Assert.True(s.ShowsEasyPartitioning);
        Assert.False(s.ShowsPartitionRebuild); // 바꾸기 전에는 없다

        Assert.True(s.ApplyEasyPartitioning());

        Assert.True(s.NeedsPartitionRebuild);
        Assert.True(s.ShowsPartitionRebuild);
        Assert.True(s.CanUsePartitionRebuild);
        Assert.Empty(s.Script.Statements); // 「실행」으로는 하지 않는다
        Assert.Equal(
            "PARTITION BY RANGE (ORDER_DATE) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH')) (PARTITION P_BEFORE_2026_11 VALUES LESS THAN (DATE '2026-11-01'))",
            s.PartitionRebuildClause);

        await s.OpenPartitionRebuildScriptAsync();
        Assert.Equal(1, opened);

        s.PartitionMethodIndex = 0; // 되돌리면 줄도 사라진다
        Assert.False(s.ShowsPartitionRebuild);
    }

    [Fact]
    public void A_new_server_converts_in_one_statement_without_the_rebuild_bar()
    {
        var s = Create(WithOrderDate(OrdersTable()));
        s.Today = () => Today;
        s.OpenPartitionRebuild = () => Task.CompletedTask;

        Assert.True(s.ApplyEasyPartitioning());

        Assert.False(s.NeedsPartitionRebuild);
        Assert.False(s.ShowsPartitionRebuild);
        Assert.Single(s.Script.Statements);
    }

    [Fact]
    public void A_server_that_cannot_partition_hides_the_convert_button_and_shows_the_reason()
    {
        const string key = "TableEdit.unsupported.x";
        var s = Create(WithOrderDate(OrdersTable()), new OracleRules { Unsupported = f => f == TableEditFeature.PartitionConversion ? key : null });
        s.OpenPartitionsPage = () => { };

        Assert.False(s.CanOpenPartitioning);
        Assert.True(s.ShowsPartitionMethodReason);
        Assert.False(s.ShowsEasyPartitioning);

        var able = Create(WithOrderDate(OrdersTable()));
        able.OpenPartitionsPage = () => { };
        Assert.True(able.CanOpenPartitioning);
        Assert.False(able.ShowsPartitionMethodReason);
    }

    [Fact]
    public async Task Object_detail_builds_the_script_from_fresh_columns_and_opens_it_in_a_sql_tab()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithOrderDate(OrdersTable());
        var scripts = new CapturingScripts();
        string? openedText = null;
        vm.TableEditDialect = new NoOnlineConversionDialect();
        vm.RebuildScripts = scripts;
        vm.DdlEditorOpener = (text, _) =>
        {
            openedText = text;
            return true;
        };
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        var edit = vm.TableEdit!;
        edit.Today = () => Today;
        Assert.True(edit.ApplyEasyPartitioning());
        Assert.True(edit.ShowsPartitionRebuild);

        Assert.True(await vm.OpenPartitionRebuildScriptAsync());

        Assert.Equal("-- rebuild", openedText);
        Assert.Equal(edit.PartitionRebuildClause, scripts.Clause);
        Assert.Equal(["ORDER_ID", "CUSTOMER_ID", "STATUS", "OLD_FLAG", "ORDER_DATE"], scripts.Columns);
        Assert.True(scripts.UsePrimaryKey);
        Assert.Equal(string.Empty, scripts.DependentDdl); // 가짜 공급자: 표 DDL = 종속 포함 DDL → 종속 부분 없음
    }

    [Fact]
    public async Task Saving_the_rebuild_script_writes_the_file()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithOrderDate(OrdersTable());
        vm.TableEditDialect = new NoOnlineConversionDialect();
        vm.RebuildScripts = new CapturingScripts();
        using var dir = new Oranda.Testing.TempDirectory("oranda-te-rebuild-");
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "orders_partition.sql");
        string? suggested = null;
        vm.PickScriptFile = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(path);
        };
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        vm.TableEdit!.Today = () => Today;
        Assert.True(vm.TableEdit.ApplyEasyPartitioning());

        Assert.Equal(path, await vm.SavePartitionRebuildScriptAsync());

        Assert.Equal("ORDERS_partition.sql", suggested);
        Assert.Equal("-- rebuild", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Bottom_bar_says_the_conversion_goes_through_the_rebuild_script_instead_of_no_changes()
    {
        var s = Old();
        Assert.Equal("바뀐 것 없음", s.SummaryText);

        Assert.True(s.ApplyEasyPartitioning());

        Assert.True(s.IsPartitionRebuildReady);
        Assert.Equal("파티션 바꾸기는 다시 만들기 스크립트로 진행합니다(검토 뒤 직접 실행)", s.SummaryText);

        s.TableComment = "주문 머리"; // 다른 변경이 있으면 건수를 두고 안내를 붙인다
        Assert.Equal("변경 1건 · 파티션 바꾸기는 다시 만들기 스크립트로 진행합니다(검토 뒤 직접 실행)", s.SummaryText);

        s.PartitionMethodIndex = 0; // 되돌림
        Assert.False(s.IsPartitionRebuildReady);
        Assert.Equal("변경 1건", s.SummaryText);
    }

    [Fact]
    public void Preview_shows_the_rebuild_script_first_and_only_for_the_state_it_was_made_for()
    {
        var s = Old();
        Assert.True(s.ApplyEasyPartitioning());
        Assert.StartsWith("-- 다시 만들기 스크립트를 만드는 중", s.PreviewText);

        var key = s.PartitionRebuildPreviewKey!;
        s.SetPartitionRebuildPreview(key, "-- rebuild");
        Assert.Equal("-- rebuild", s.PreviewText);

        s.TableComment = "주문 머리";
        Assert.StartsWith("-- rebuild" + Environment.NewLine + Environment.NewLine + "-- 다른 변경(다시 만들기 스크립트 뒤에 실행)", s.PreviewText);
        Assert.Contains("COMMENT ON TABLE", s.PreviewText);

        s.SetPartitionRebuildPreview("다른 절", "-- stale"); // 맞지 않는 상태의 글은 보이지 않는다
        Assert.Null(s.PartitionRebuildPreview);
        Assert.DoesNotContain("-- stale", s.PreviewText);
    }

    [Fact]
    public async Task Object_detail_fills_the_preview_saves_the_rebuild_script_and_blocks_run_and_bundle()
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithOrderDate(OrdersTable());
        vm.TableEditDialect = new NoOnlineConversionDialect();
        vm.RebuildScripts = new CapturingScripts();
        using var dir = new Oranda.Testing.TempDirectory("oranda-te-rebuild-bar-");
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "orders_partition.sql");
        string? suggested = null;
        vm.PickScriptFile = (name, _) =>
        {
            suggested = name;
            return Task.FromResult<string?>(path);
        };
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        var edit = vm.TableEdit!;
        edit.Today = () => Today;
        Assert.False(vm.CanSaveTableEditScript);

        Assert.True(edit.ApplyEasyPartitioning());
        await vm.PartitionRebuildPreviewTask;

        Assert.Equal("-- rebuild", edit.PreviewText);
        Assert.True(vm.CanSaveTableEditScript); // 저장·복사·편집기로 열기
        Assert.False(vm.CanRunTableEdit);
        Assert.False(vm.CanAddToBundleNow);
        Assert.Contains("한 문장으로 파티션 테이블로 바꾸지 못해", vm.RunTableEditTip);
        Assert.Null(await vm.RunTableEditAsync());

        Assert.Equal(path, await vm.SaveTableEditScriptAsync());
        Assert.Equal("ORDERS_partition.sql", suggested);
        Assert.Equal("-- rebuild", await File.ReadAllTextAsync(path));

        edit.TableComment = "주문 머리"; // 다른 변경도 있으면 다시 만들기 뒤에 붙는다
        Assert.False(vm.CanRunTableEdit);
        Assert.Equal(path, await vm.SaveTableEditScriptAsync());
        var text = await File.ReadAllTextAsync(path);
        Assert.StartsWith("-- rebuild", text);
        Assert.True(text.IndexOf("COMMENT ON TABLE", StringComparison.Ordinal) > text.IndexOf("-- 다른 변경", StringComparison.Ordinal));

        edit.PartitionMethodIndex = 0; // 되돌리면 실행이 다시 된다
        Assert.True(vm.CanRunTableEdit);
        Assert.DoesNotContain("다시 만들기", vm.RunTableEditTip);
    }

    [Fact]
    public Task Bottom_bar_shows_the_note_the_rebuild_preview_and_a_disabled_run_with_its_reason() => HeadlessHost.Run(async () =>
    {
        var (vm, meta, _) = CreateDetail();
        meta.Table = WithOrderDate(OrdersTable());
        vm.TableEditDialect = new NoOnlineConversionDialect();
        vm.RebuildScripts = new CapturingScripts();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1200, Height = 760 };
        w.Show();
        try
        {
            vm.TableEdit!.Today = () => Today;
            Assert.True(vm.TableEdit.ApplyEasyPartitioning());
            await vm.PartitionRebuildPreviewTask;
            HeadlessHost.Settle(w);

            Assert.Equal("파티션 바꾸기는 다시 만들기 스크립트로 진행합니다(검토 뒤 직접 실행)", view.FindControl<TextBlock>("TableEditSummaryText")!.Text);
            Assert.True(view.FindControl<Button>("SaveScriptButton")!.IsEnabled);
            Assert.True(view.FindControl<Button>("CopyEditDdlButton")!.IsEnabled);
            Assert.True(view.FindControl<Button>("OpenEditDdlButton")!.IsEnabled);
            var run = view.FindControl<Button>("RunTableEditButton")!;
            Assert.False(run.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(run));
            Assert.Contains("다시 만들기 스크립트를 검토한 뒤 직접 실행", ToolTip.GetTip(run) as string);
            Assert.Equal("-- rebuild", view.FindControl<Oranda.App.Features.SqlEditor.SqlCodeView>("TableEditDdlView")!.Text);
        }
        finally
        {
            w.Close();
        }
    });

    /// <summary>가짜 방언 + 한 문장 바꾸기 없음(Tibero 꼴).</summary>
    private sealed class NoOnlineConversionDialect : ITableEditDialect
    {
        private readonly FakeDialect _inner = new();

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames => _inner.TypeNames;

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;

        public bool SupportsOnlinePartitionConversion(ServerInfo? server) => false;

        public bool IsServerName(string name) => _inner.IsServerName(name);

        public bool IsReservedWord(string upperName) => false;

        public string NormalizeType(ColumnDraft column) => _inner.NormalizeType(column);

        public string? NormalizeDefault(string? serverText) => _inner.NormalizeDefault(serverText);

        public string? NormalizeExpression(string? serverText) => _inner.NormalizeExpression(serverText);

        public string? NormalizeCondition(string? serverText) => _inner.NormalizeCondition(serverText);
    }

    /// <summary>받은 인자를 남기고 정해진 글을 돌려주는 가짜.</summary>
    private sealed class CapturingScripts : IColumnReorderScripts
    {
        public string? Clause { get; private set; }

        public List<string> Columns { get; } = [];

        public bool UsePrimaryKey { get; private set; }

        public string? DependentDdl { get; private set; } = "(불리지 않음)";

        public Dbms Dbms => Dbms.Oracle;

        public string RebuildPartitioned(
            DbObjectRef table, IReadOnlyList<ColumnInfo> columns, bool enterprise, bool usePrimaryKey, string? dependentDdl, string partitionClause)
        {
            Clause = partitionClause;
            Columns.Clear(); // 마지막 부름(미리보기도 부른다)
            Columns.AddRange(columns.Select(c => c.Name));
            UsePrimaryKey = usePrimaryKey;
            DependentDdl = dependentDdl;
            return "-- rebuild";
        }

        public string DependentPart(string tableDdl, string fullDdl) => ColumnReorderBuilder.DependentPart(tableDdl, fullDdl);

        public IReadOnlyList<string> BlockReasons(ColumnReorderMethod method, IReadOnlyList<ColumnInfo> columns, ColumnReorderTraits traits, bool supportsInvisibleColumns) =>
            throw new NotSupportedException();

        public int FirstDifference(IReadOnlyList<string> original, IReadOnlyList<string> desired) => throw new NotSupportedException();

        public IReadOnlyList<SqlStatement> InvisibleVisible(DbObjectRef table, IReadOnlyList<string> original, IReadOnlyList<string> desired) =>
            throw new NotSupportedException();

        public SqlStatement AsSingleBlock(DbObjectRef table, IReadOnlyList<SqlStatement> statements) => throw new NotSupportedException();

        public string ToText(IReadOnlyList<SqlStatement> statements) => throw new NotSupportedException();

        public string Rebuild(
            DbObjectRef table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<string> desired, bool enterprise, bool usePrimaryKey, string? dependentDdl) =>
            throw new NotSupportedException();

        public IReadOnlyList<ColumnReorderRiskHit> FilterRiskHits(IEnumerable<SourceSearchHit> lines, DbObjectRef table) => throw new NotSupportedException();
    }
}
