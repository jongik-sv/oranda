using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.App.Tests.Shell;
using static Oranda.App.Tests.Features.TableEdit.TableEditTestSupport;

namespace Oranda.App.Tests.Features.TableEdit;

/// <summary>
/// 2차 화면 칸(T15, 설계 18.1·18.5·18.6·18.8): 신원·숨김·도메인, 방언 미지원 흐림+이유, 인덱스 종류·식, 테이블 이름 바꾸기·규칙 이름,
/// 실행 출처 TableEditor, 「묶음에 넣기 ▾」, 기능 끔. 실제 Core 엔진으로 문장까지 본다.
/// </summary>
public class TableEditPhase2CellTests
{
    private static TableEditSession Create(ITableEditDialect? dialect = null, TableInfo? table = null)
    {
        dialect ??= new Rules();
        var engine = CoreTableEditEngine.Instance;
        return new TableEditSession(engine.FromTable(table ?? OrdersTable(), dialect), dialect, new TableEditContext(128, ConnectionEnvironment.Development), engine);
    }

    private static string Sql(TableEditSession edit) => string.Join("\n", edit.Script.Statements.Select(s => s.Statement.Text));

    [Fact]
    public void Identity_on_a_new_column_writes_generation_and_options()
    {
        var edit = Create();
        var id = edit.AddColumn()!;
        id.Name = "ITEM_NO";
        id.DataType = "NUMBER";
        id.IdentityIndex = 1;
        id.IdentityStartText = "100";
        id.IdentityIncrementText = "5";

        Assert.Equal(IdentityGeneration.Always, id.Column.Identity!.Generation);
        Assert.Equal((100L, 5L), (id.Column.Identity.StartWith!.Value, id.Column.Identity.IncrementBy!.Value));
        Assert.Contains("GENERATED ALWAYS AS IDENTITY", Sql(edit), StringComparison.Ordinal);
        Assert.Contains("100", Sql(edit), StringComparison.Ordinal);

        id.IdentityStartText = "x";
        Assert.True(id.HasError);
        id.IdentityIndex = 0;
        Assert.Null(id.Column.Identity);
        Assert.False(id.CanEditIdentityOptions);
    }

    [Fact]
    public void Unknown_identity_attributes_only_allow_dropping_the_identity()
    {
        var table = OrdersTable() with
        {
            Columns = [.. OrdersTable().Columns.Select(c => c.Name == "ORDER_ID" ? c with { IsIdentity = true, Identity = null } : c)],
        };
        var edit = Create(table: table);
        var row = edit.Columns.Single(c => c.Name == "ORDER_ID");

        Assert.True(row.IsIdentityUnknown);
        Assert.Equal(4, row.IdentityIndex);
        Assert.False(row.CanEditIdentity);
        Assert.True(row.CanDropUnknownIdentity);
        Assert.Contains("신원 없애기", row.IdentityTip, StringComparison.Ordinal);
        row.IdentityIndex = 1; // 고르기는 안 먹는다
        Assert.False(edit.IsDirty);

        row.DropIdentity();

        Assert.Contains("DROP IDENTITY", Sql(edit), StringComparison.Ordinal);
    }

    [Fact]
    public void Dialect_unsupported_features_grey_out_with_the_reason()
    {
        var edit = Create(new Rules { Unsupported = { TableEditFeature.IdentityColumn, TableEditFeature.InvisibleColumn, TableEditFeature.DomainColumn, TableEditFeature.BitmapIndex, TableEditFeature.RenameTable } });
        var row = edit.Columns[1];

        Assert.False(row.CanEditIdentity);
        Assert.False(row.CanEditInvisible);
        Assert.False(row.CanEditDomain);
        Assert.Equal("TableEdit.unsupported.test", row.InvisibleReason); // 키가 문구에 없으면 키 그대로(UiText.Get 대체)
        Assert.NotNull(row.IdentityTip);
        row.IsInvisible = true;
        Assert.False(edit.IsDirty);
        Assert.False(edit.CanRenameTable);
        Assert.NotNull(edit.RenameTableReason);

        var ix = edit.Indexes.Single(i => i.Name == "IX_ORDERS_01");
        Assert.Contains("BITMAP", ix.KindTip, StringComparison.Ordinal);
        ix.KindIndex = 2;
        Assert.True(ix.HasError); // 못 만드는 종류는 고르지 않는다
        Assert.Equal(0, ix.KindIndex);
    }

    [Fact]
    public void Invisible_and_domain_cells_produce_their_own_modify()
    {
        var edit = Create();
        var status = edit.Columns.Single(c => c.Name == "STATUS");
        status.IsInvisible = true;
        status.Domain = "sample.status_d";

        Assert.True(status.IsInvisibleChanged);
        Assert.True(status.IsDomainChanged);
        Assert.Equal("SAMPLE.STATUS_D", status.Column.Domain);
        Assert.Contains("INVISIBLE", Sql(edit), StringComparison.Ordinal);
        Assert.Contains("DOMAIN", Sql(edit), StringComparison.Ordinal);
    }

    [Fact]
    public void Function_based_index_keeps_expressions_and_switching_back_needs_them_removed_first()
    {
        var edit = Create();
        var ix = edit.AddIndex()!;
        ix.KindIndex = 1;
        ix.ColumnsText = "UPPER(STATUS), customer_id DESC";

        Assert.Equal("UPPER(STATUS)", ix.Index.Columns[0].Expression);
        Assert.Equal("c2", ix.Index.Columns[1].ColumnKey);
        Assert.True(ix.Index.Columns[1].Descending);
        Assert.Contains("UPPER(STATUS)", Sql(edit), StringComparison.Ordinal);

        ix.KindIndex = 0; // 식이 있으면 일반으로 바꾸지 않는다(식을 말없이 버리지 않게)
        Assert.True(ix.HasError);
        ix.ColumnsText = "customer_id DESC";
        ix.KindIndex = 0;
        Assert.Equal(IndexKind.Normal, ix.Index.Kind);
        ix.ColumnsText = "UPPER(STATUS)";
        Assert.True(ix.HasError); // 일반 인덱스에 식은 안 받는다
    }

    [Fact]
    public void Table_rename_is_last_and_rule_names_follow_only_when_checked()
    {
        var edit = Create();
        edit.TableName = "orders_hist";

        Assert.True(edit.IsTableNameChanged);
        Assert.EndsWith("RENAME TO ORDERS_HIST", edit.Script.Statements[^1].Statement.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PK_ORDERS_HIST", Sql(edit), StringComparison.Ordinal);

        edit.RenameRuleNames = true;

        Assert.Contains("PK_ORDERS_HIST", Sql(edit), StringComparison.Ordinal);
        Assert.EndsWith("RENAME TO ORDERS_HIST", edit.Script.Statements[^1].Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_are_recorded_as_table_editor_source()
    {
        Assert.Equal(Oranda.Core.History.HistorySource.TableEditor, TableEditRunner.Source);
    }

    [Fact]
    public async Task Add_to_bundle_checks_first_then_adds_every_previewed_statement()
    {
        var (vm, _, _) = CreateDetail();
        var bundles = new FakeBundles();
        vm.ChangeBundles = bundles;
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        Assert.True(vm.CanAddToBundle);
        Assert.False(vm.CanAddToBundleNow); // 문장이 없다
        vm.TableEdit!.TableComment = "새 주석";
        vm.TableEdit.Columns[0].Comment = "번호";

        var result = await vm.AddTableEditToBundleAsync(newBundle: false);

        Assert.Equal(ChangeBundleAddOutcome.Done, result!.Outcome);
        var (origin, drafts, source) = Assert.Single(bundles.Added);
        Assert.Equal(vm.TableEdit.Script.Statements.Select(s => s.Statement.Text), drafts.Select(d => d.Text));
        Assert.All(drafts, d => Assert.Equal("SAMPLE", d.TargetOwner));
        Assert.Equal(ChangeBundleItemSource.TableEditor, source);
        Assert.Equal("dev-example", origin.ConnectionName);
        Assert.Contains("2문장을 넣었다", vm.StatusText, StringComparison.Ordinal);
        Assert.True(vm.IsTableEditing); // 실행하지 않았으니 편집은 그대로
    }

    [Fact]
    public async Task Add_to_bundle_is_blocked_by_failing_checks_and_new_bundle_asks_a_name()
    {
        var (vm, _, _) = CreateDetail();
        var bundles = new FakeBundles();
        vm.ChangeBundles = bundles;
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        await vm.SetTableEditingAsync(true);
        vm.TableEdit!.TableComment = "새 주석";
        var engine = (FakeTableEditEngine)vm.TableEditEngine!;
        engine.Findings = [new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, "$table") { Subject = "X" }];
        vm.TableEditChecks = new TableEditRunTests.FakeChecks();

        Assert.Null(await vm.AddTableEditToBundleAsync(newBundle: false));
        Assert.Empty(bundles.Added);
        Assert.True(vm.HasBlockingFindings);

        engine.Findings = [];
        vm.PromptBundleName = (initial, _) => Task.FromResult<string?>(null);
        Assert.Null(await vm.AddTableEditToBundleAsync(newBundle: true)); // 이름 취소
        Assert.Empty(bundles.Created);

        vm.PromptBundleName = (initial, _) => Task.FromResult<string?>("11/20 주문 의뢰");
        await vm.AddTableEditToBundleAsync(newBundle: true);
        Assert.Equal("11/20 주문 의뢰", Assert.Single(bundles.Created));
        Assert.Single(bundles.Added);
    }

    [Fact]
    public async Task Turning_the_feature_off_hides_the_edit_toggle_and_the_command_does_nothing()
    {
        var (vm, _, _) = CreateDetail();
        var services = new Services(new Gate(off: FeatureIds.TableEditor));
        var detail = new ObjectDetailViewModel(services)
        {
            TableEditEngine = vm.TableEditEngine,
            TableEditDialect = vm.TableEditDialect,
            InvalidateTableCache = vm.InvalidateTableCache,
        };
        services.Meta = new FakeMeta();
        await detail.OpenAsync(new FakeSession(), Orders, showDdl: false);

        Assert.False(detail.CanToggleTableEdit);
        var handler = new TableEditToggleCommandHandler { Target = () => detail };
        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TableEditCommands.ToggleId, FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.False(detail.IsTableEditing);
        Assert.False(await detail.SetTableEditingAsync(true));
    }

    [Fact]
    public async Task Toggle_command_turns_editing_on_and_off_for_the_active_detail()
    {
        var (vm, _, _) = CreateDetail();
        await vm.OpenAsync(new FakeSession(), Orders, showDdl: false);
        var handler = new TableEditToggleCommandHandler { Target = () => vm };

        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TableEditCommands.ToggleId, FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.True(vm.IsTableEditing);
        await handler.ExecuteAsync(new Oranda.Core.Shell.CommandContext(TableEditCommands.ToggleId, FakeContextKeys.AllTrue), CancellationToken.None);
        Assert.False(vm.IsTableEditing);
    }

    private static TableInfo PartitionedOrders() => OrdersTable() with
    {
        Columns = [.. OrdersTable().Columns, new ColumnInfo("ORDER_DATE", 5, "DATE") { IsNullable = false }],
        IsPartitioned = true,
        Partitioning = new PartitioningInfo("RANGE", ["ORDER_DATE"],
        [
            new PartitionInfo("P_2024_01", 1, "DATE '2024-02-01'") { NumRows = 95020 },
            new PartitionInfo("P_2026_10", 2, "DATE '2026-11-01'") { NumRows = 190115 },
        ]),
    };

    [Fact]
    public async Task Partition_checks_block_key_column_drops_and_warn_about_lost_rows()
    {
        var edit = Create(table: PartitionedOrders());
        edit.SetPartitions(PartitionedOrders());
        edit.ToggleDrop(edit.Columns.Single(c => c.Name == "ORDER_DATE").Key);

        var block = Assert.Single(edit.PartitionFindings(), f => f.Severity == CheckSeverity.Block);
        Assert.Equal("ORDER_DATE", block.Subject);
        Assert.Contains("지울 수 없습니다", CheckFindingText.Format(block), StringComparison.Ordinal);

        edit.RevertAll();
        edit.UpdatePartitions(d => d with { Partitions = [.. d.Partitions.Select(p => p.Name == "P_2024_01" ? p with { IsDropped = true } : p)] });
        var findings = await new TableEditRunner(null).CheckAsync(new FakeSession(), Orders, edit, CancellationToken.None);

        var warn = Assert.Single(findings, f => f.Code == CheckCode.Irreversible && f.Subject is null);
        Assert.Contains("95,020", CheckFindingText.Format(warn), StringComparison.Ordinal);
        Assert.StartsWith("⚠", CheckFindingText.Format(warn), StringComparison.Ordinal);
        Assert.Contains("DROP PARTITION", Sql(edit), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unfinished_new_partition_row_blocks_running()
    {
        var edit = Create(table: PartitionedOrders());
        edit.SetPartitions(PartitionedOrders());
        edit.UpdatePartitions(d => d with { Partitions = [.. d.Partitions, new Oranda.Core.Schema.TableEdit.Partitions.PartitionDraft(DraftKeys.New(), "P_NEW1")] });

        Assert.Contains(edit.PartitionFindings(), f => f.Severity == CheckSeverity.Block && f.Code == CheckCode.InvalidDefinition);
    }

    private sealed class Rules : ITableEditDialect
    {
        public HashSet<TableEditFeature> Unsupported { get; } = [];

        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NUMBER", "CHAR"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => Unsupported.Contains(feature) ? "TableEdit.unsupported.test" : null;
    }

    private sealed class FakeBundles : IChangeBundleService
    {
        public List<(ChangeBundleOrigin Origin, IReadOnlyList<ChangeBundleDraft> Drafts, ChangeBundleItemSource Source)> Added { get; } = [];

        public List<string> Created { get; } = [];

        public Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct) => Task.FromResult<ChangeBundleInfo?>(null);

        public ChangeBundleOrigin? OriginOf(Guid sessionId) => null;

        public Task<ChangeBundleInfo?> CreateAsync(string name, ChangeBundleOrigin? origin, bool makeCurrent, CancellationToken ct)
        {
            Created.Add(name);
            return Task.FromResult<ChangeBundleInfo?>(new ChangeBundleInfo(1, name, DateTimeOffset.Now, DateTimeOffset.Now, null, [], makeCurrent));
        }

        public Task<ChangeBundleAddResult> AddAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements, ChangeBundleItemSource source, CancellationToken ct)
        {
            Added.Add((origin, statements, source));
            return Task.FromResult(new ChangeBundleAddResult(ChangeBundleAddOutcome.Done, 1, statements.Count, 0, 0, 0));
        }

        public Task<ChangeBundleAddResult> AddExecutedAsync(ChangeBundleOrigin origin, SqlStatement statement, ChangeBundleItemSource source, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordRunAsync(long itemId, ChangeBundleOrigin origin, bool succeeded, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Gate(string off) : IFeatureGate
    {
        public bool IsEnabled(string featureId) => featureId != off;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;

        public bool ReadOnlyRole => false;

        public IReadOnlyCollection<string> PendingRestart { get; } = [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class Services(IFeatureGate gate) : IServiceProvider
    {
        public IMetadataProvider? Meta { get; set; }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IFeatureGate) ? gate
            : serviceType == typeof(IMetadataProvider) ? Meta
            : null;
    }
}
