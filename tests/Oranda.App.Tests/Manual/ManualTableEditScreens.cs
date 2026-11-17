using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeBundle;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.TableEdit;
using Oranda.App.Tests.Features.ChangeBundle;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 테이블 편집기·반영 묶음 화면 캡처 생성기(T12). 샘플 DB 없이 그린다: 예시 테이블(SAMPLE.ORDERS)을 공급자 대신 넣고,
/// 문장·검사 판정은 실제 Core 엔진(<see cref="CoreTableEditEngine"/>·<see cref="ColumnConversionBuilder"/>)이 만든다. 검사 읽기는 지어낸 건수다. 실행은 하지 않는다.
/// 번호의 뜻은 <c>schema/table-editor.md</c> 「편집 화면」(1~5), <c>tools/db-tools.md</c> 「반영 묶음 창」(1~4)과 같아야 한다.
/// 캡처는 <see cref="ManualCapture.Enabled"/> 일 때만(평소 시험·CI 는 건너뜀).
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualTableEditScreens
{
    private static readonly DbObjectRef Orders = new("SAMPLE", "ORDERS", "TABLE");

    private static readonly ServerInfo Server19 = new("19.0.0.0.0", "ORCL", "db-example", "ORCLPDB1", "ORCL", "ORCLPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false), Edition: "Enterprise Edition");

    private static void Pump() => HeadlessHost.Pump(4);

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static Window Show(Control view, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        return window;
    }

    /// <summary>예시 테이블: 열 7개(가상 없음), PK·FK·CHECK, 일반 인덱스, 저장 속성·LOB.</summary>
    private static TableInfo OrdersTable(bool partitioned = false) => new(Orders,
    [
        new ColumnInfo("ORDER_ID", 1, "NUMBER") { Precision = 10, IsNullable = false, Comment = "주문 번호" },
        new ColumnInfo("CUSTOMER_ID", 2, "NUMBER") { Precision = 8, IsNullable = false, Comment = "고객" },
        new ColumnInfo("ORDER_DATE", 3, "DATE") { IsNullable = false, DefaultValue = "SYSDATE", Comment = "주문일" },
        new ColumnInfo("STATUS", 4, "VARCHAR2") { Length = 50, CharSemantics = CharSemantics.Bytes, Comment = "상태" },
        new ColumnInfo("TOTAL_AMOUNT", 5, "NUMBER") { Precision = 12, Scale = 2, Comment = "합계" },
        new ColumnInfo("OLD_FLAG", 6, "CHAR") { Length = 1, CharSemantics = CharSemantics.Bytes },
        new ColumnInfo("MEMO", 7, "CLOB") { Comment = "비고" },
    ])
    {
        Comment = "주문",
        Tablespace = "USERS",
        Storage = new TableStorage { PctFree = 10, InitTrans = 1, Compression = TableCompression.None, Logging = true, Parallel = 1 },
        Lobs = [new LobInfo("MEMO", new LobStorage { Kind = LobStorageKind.SecureFile, Tablespace = "USERS", Compression = LobCompression.None, Deduplicate = false, InRow = true })],
        Constraints =
        [
            new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ORDER_ID"]),
            new ConstraintInfo("FK_ORDERS_01", ConstraintKind.ForeignKey, ["CUSTOMER_ID"]) { ReferencedTable = new DbObjectRef("SAMPLE", "CUSTOMERS", "TABLE"), ReferencedColumns = ["CUSTOMER_ID"] },
            new ConstraintInfo("CK_ORDERS_01", ConstraintKind.Check, ["STATUS"]) { Condition = "STATUS IN ('NEW','PAID','SHIPPED')" },
        ],
        Indexes =
        [
            new IndexInfo("PK_ORDERS", true, ["ORDER_ID"]) { ConstraintName = "PK_ORDERS", IndexType = "NORMAL" },
            new IndexInfo("IX_ORDERS_01", false, ["ORDER_DATE"]) { IndexType = "NORMAL" },
        ],
        IsPartitioned = partitioned,
        Partitioning = partitioned
            ? new PartitioningInfo("RANGE", ["ORDER_DATE"],
            [
                new PartitionInfo("P_2024_01", 1, "TO_DATE(' 2024-02-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')") { NumRows = 95020, Tablespace = "USERS" },
                new PartitionInfo("P_2026_09", 2, "TO_DATE(' 2026-10-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')") { NumRows = 182400, Tablespace = "USERS" },
                new PartitionInfo("P_2026_10", 3, "TO_DATE(' 2026-11-01 00:00:00', 'SYYYY-MM-DD HH24:MI:SS', 'NLS_CALENDAR=GREGORIAN')") { NumRows = 190115, Tablespace = "USERS" },
            ])
            : null,
    };

    private static Oranda.Core.Metadata.ObjectDetail Detail(bool partitioned) => new(Orders,
    [
        new DetailSection("일반", ["항목", "값"], [["상태", "VALID"], ["행 수(통계)", 1240000]]),
        new DetailSection("설명", ["항목", "값"], [["테이블 주석", "주문"]]),
        new DetailSection("열", ["#", "이름", "형식"], [[1, "ORDER_ID", "NUMBER(10)"]]),
        new DetailSection("제약", ["이름", "종류"], [["PK_ORDERS", "P"]]),
        new DetailSection("인덱스", ["이름", "유일"], [["PK_ORDERS", "Y"]]),
        .. partitioned ? new[] { new DetailSection("파티션", ["이름", "상한값"], [["P_2024_01", "2024-02-01"]]) } : [],
        new DetailSection("통계", ["항목", "값"], []),
    ]);

    /// <summary>편집을 켠 객체 상세(실제 Core 엔진·Oracle 규칙, 개발 접속).</summary>
    private static async Task<(ObjectDetailViewModel Vm, ExampleMeta Meta)> OpenAsync(bool partitioned = false, ConnectionEnvironment env = ConnectionEnvironment.Development)
    {
        var meta = new ExampleMeta(OrdersTable(partitioned), Detail(partitioned));
        var vm = new ObjectDetailViewModel(meta)
        {
            TableEditEngine = CoreTableEditEngine.Instance,
            TableEditDialect = new OracleRules(),
            InvalidateTableCache = (_, _, _) => Task.CompletedTask,
            TableEditChecks = new ExampleChecks(),
            ChangeBundles = new MemoryBundleService(new MemoryBundleStore()),
        };
        await vm.OpenAsync(new ExampleSession(env), Orders, showDdl: false);
        Assert.True(await vm.SetTableEditingAsync(true));
        return (vm, meta);
    }

    /// <summary>열 페이지 편집: STATUS 길이 줄임(노랑), OLD_FLAG 지움(빨강), 새 열 TOTAL_VAT(초록, 가상 식).</summary>
    private static void EditColumns(TableEditSession edit)
    {
        edit.Columns.Single(c => c.Name == "STATUS").LengthText = "20";
        edit.ToggleDrop(edit.Columns.Single(c => c.Name == "OLD_FLAG").Key);
        var vat = edit.AddColumn()!;
        vat.Name = "TOTAL_VAT";
        vat.DataType = "NUMBER";
        vat.VirtualExpression = "TOTAL_AMOUNT * 0.1";
        vat.Comment = "부가세";
    }

    private static async Task ShowFindingsAsync(ObjectDetailViewModel vm, ExampleChecks checks)
    {
        var runner = new TableEditRunner(null) { Checks = checks };
        vm.TableEditFindings = await runner.CheckAsync(new ExampleSession(ConnectionEnvironment.Development), Orders, vm.TableEdit!, CancellationToken.None);
    }

    [Fact]
    public Task Table_edit_columns() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (vm, _) = await OpenAsync();
        EditColumns(vm.TableEdit!);
        await ShowFindingsAsync(vm, new ExampleChecks());
        var view = new ObjectDetailView { DataContext = vm };
        var window = Show(view, 1560, 740);
        try
        {
            ManualCapture.Save(window, "table-edit-columns",
            [
                new ManualCapture.Callout(Named<Control>(view, "TableEditToggle"), 1),
                new ManualCapture.Callout(Named<Control>(view, "TableEditView"), 2, 600, 340),
                new ManualCapture.Callout(Named<Control>(view, "TableEditDdlView"), 3, -14, 10),
                new ManualCapture.Callout(Named<Control>(view, "TableEditFindingsList"), 4, -14, 8),
                new ManualCapture.Callout(Named<Control>(view, "EnvironmentText"), 5, -6),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_edit_new() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var meta = new ExampleMeta(OrdersTable(), Detail(partitioned: false));
        var vm = new ObjectDetailViewModel(meta)
        {
            TableEditEngine = CoreTableEditEngine.Instance,
            TableEditDialect = new OracleRules(),
            InvalidateTableCache = (_, _, _) => Task.CompletedTask,
            TableEditChecks = new ExampleChecks(),
            ChangeBundles = new MemoryBundleService(new MemoryBundleStore()),
        };
        Assert.True(await vm.OpenNewTableAsync(new ExampleSession(ConnectionEnvironment.Development), "SAMPLE", ["SAMPLE"]));
        vm.NewTableName = "ORDER_ITEMS";
        vm.NewTableComment = "주문 상품";
        var edit = vm.TableEdit!;
        foreach (var (name, type, size, required, comment) in new[]
        {
            ("ORDER_ID", "NUMBER", "10", true, "주문 번호"),
            ("LINE_NO", "NUMBER", "4", true, "줄 번호"),
            ("PRODUCT_CODE", "VARCHAR2", "20", true, "상품 코드"),
            ("QTY", "NUMBER", "8", false, "수량"),
        })
        {
            var column = edit.AddColumn()!;
            column.Name = name;
            column.DataType = type;
            column.LengthText = size;
            column.IsNullable = !required;
            column.Comment = comment;
        }

        edit.Columns.Single(c => c.Name == "QTY").DefaultValue = "1";
        edit.AddConstraint(ConstraintKind.PrimaryKey)!.ColumnsText = "ORDER_ID, LINE_NO";
        var view = new ObjectDetailView { DataContext = vm };
        var window = Show(view, 1560, 740);
        try
        {
            ManualCapture.Save(window, "table-edit-new");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_edit_check() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (vm, _) = await OpenAsync();
        var edit = vm.TableEdit!;
        edit.Columns.Single(c => c.Name == "STATUS").LengthText = "20";
        edit.Columns.Single(c => c.Name == "TOTAL_AMOUNT").IsNullable = false;
        edit.ToggleDrop(edit.Columns.Single(c => c.Name == "OLD_FLAG").Key);
        await ShowFindingsAsync(vm, new ExampleChecks { Overflow = 37, Nulls = 12, Dependents = [new DbObjectRef("SAMPLE", "V_ORDER_SUMMARY", "VIEW"), new DbObjectRef("SAMPLE", "TRG_ORDERS_AUDIT", "TRIGGER")] });
        var view = new ObjectDetailView { DataContext = vm };
        var window = Show(view, 1560, 740);
        try
        {
            ManualCapture.Save(window, "table-edit-check");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_edit_storage() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (vm, _) = await OpenAsync();
        vm.SelectedSection = vm.Sections.Single(s => s.Title == ObjectDetailViewModel.StorageTitle);
        vm.TableEdit!.Update(d => d with
        {
            Tablespace = "DATA01",
            Storage = d.Storage with { PctFree = 20, Compression = TableCompression.Basic },
            Columns = [.. d.Columns.Select(c => c.Name == "MEMO" ? c with { Lob = (c.Lob ?? new LobStorage()) with { Tablespace = "LOB01" } } : c)],
        });
        var view = new ObjectDetailView { DataContext = vm };
        var window = Show(view, 1560, 740);
        try
        {
            ManualCapture.Save(window, "table-edit-storage");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_edit_partition() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (vm, _) = await OpenAsync(partitioned: true);
        vm.SelectedSection = vm.Sections.Single(s => s.Title == ObjectDetailViewModel.PartitionsTitle);
        vm.TableEdit!.UpdatePartitions(d => d with
        {
            Partitions =
            [
                .. d.Partitions.Select(p => p.Name == "P_2024_01" ? p with { IsDropped = true } : p),
                new Oranda.Core.Schema.TableEdit.Partitions.PartitionDraft(DraftKeys.New(), "P_2026_11") { Bound = "DATE '2026-12-01'" },
            ],
        });
        var view = new ObjectDetailView { DataContext = vm };
        var window = Show(view, 1560, 740);
        try
        {
            ManualCapture.Save(window, "table-edit-partition");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Table_edit_convert() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (vm, _) = await OpenAsync();
        var status = vm.TableEdit!.Columns.Single(c => c.Name == "STATUS");
        status.DataType = "NUMBER";
        status.LengthText = "12";
        var conversion = new ColumnConversionViewModel(new ExampleSession(ConnectionEnvironment.Development), vm.TableEdit, status.Key, OrdersTable(),
            new ColumnConversionServices
            {
                Checks = new ExampleChecks(),
                Conversions = new ExampleConversions(),
                OpenEditor = (_, _) => true,
            });
        await conversion.CheckAsync();
        var window = new ColumnConversionWindow { DataContext = conversion, Width = 1000, Height = 640 };
        window.Show();
        Pump();
        try
        {
            ManualCapture.Save(window, "table-edit-convert");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Change_bundle() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var store = new MemoryBundleStore();
        var bundle = (await store.CreateAsync("11/20 주문 상품 의뢰", Dbms.Oracle, ["SAMPLE"], makeCurrent: true, default))!;
        string[] edited =
        [
            "CREATE TABLE SAMPLE.ORDER_ITEMS (ORDER_ID NUMBER(10) NOT NULL, LINE_NO NUMBER(4) NOT NULL)",
            "COMMENT ON TABLE SAMPLE.ORDER_ITEMS IS '주문 상품'",
        ];
        foreach (var text in edited)
        {
            await store.AddItemAsync(bundle.Id, new NewChangeBundleItem(text, ChangeBundleItemSource.TableEditor, "SAMPLE", "ORDER_ITEMS"), default);
        }

        await store.AddItemAsync(bundle.Id, new NewChangeBundleItem("ALTER TABLE SAMPLE.ORDERS ADD (ITEM_CNT NUMBER(4))", ChangeBundleItemSource.SqlEditor, "SAMPLE", "ORDERS"), default);
        await store.AddItemAsync(bundle.Id, new NewChangeBundleItem("ALTER TABLE SAMPLE.ORDERS DROP COLUMN ITEM_CNT2", ChangeBundleItemSource.SqlEditor, "SAMPLE", "ORDERS"), default);
        await store.AddItemAsync(bundle.Id, new NewChangeBundleItem("CREATE INDEX SAMPLE.IX_ORDER_ITEMS_01 ON SAMPLE.ORDER_ITEMS (ORDER_ID, QTY DESC)", ChangeBundleItemSource.TableEditor, "SAMPLE", "ORDER_ITEMS"), default);
        var items = await store.ListItemsAsync(bundle.Id, default);
        await store.SetCheckedAsync(items[3].Id, false, default);
        var dev = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(9));
        foreach (var item in items)
        {
            await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Development, "dev-example", dev, true), default);
        }

        foreach (var item in items.Take(2))
        {
            await store.AddRunAsync(new ChangeBundleRun(item.Id, ConnectionEnvironment.Test, "test-example", dev.AddDays(1), true), default);
        }

        using var vm = ChangeBundleViewModel.ForTests(store, new MemoryBundleService(store));
        await vm.ReloadAsync();
        var view = new ChangeBundleView { DataContext = vm };
        var window = Show(view, 1100, 380);
        try
        {
            ManualCapture.Save(window, "change-bundle",
            [
                new ManualCapture.Callout(Named<Control>(view, "BundleBox"), 1, 165, -6),
                new ManualCapture.Callout(Named<Control>(view, "RunButton"), 2, 70, -4),
                new ManualCapture.Callout(Named<Control>(view, "List"), 3, 520, 220),
                new ManualCapture.Callout(view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("panel-status")), 4, 150, 12),
            ]);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 예시 가짜 --------------------------------------------------------------------------

    private sealed class OracleRules : ITableEditDialect
    {
        public Dbms Dbms => Dbms.Oracle;

        public IReadOnlyList<string> TypeNames { get; } = ["VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "NUMBER", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "CLOB", "NCLOB", "BLOB", "RAW"];

        public bool SupportsVirtualColumnModify => true;

        public bool PurgesOnDrop => false;

        public int MaxIdentifierBytes(ServerInfo? server) => 128;

        public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => null;
    }

    private sealed class ExampleSession(ConnectionEnvironment environment) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev-example", "app_dev", "db.example.com:1521/devsvc", Id: Guid.NewGuid(), Environment: environment);

        public ServerInfo Server => Server19;

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "ORCL/ORCLPDB1/APP_DEV";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExampleMeta(TableInfo table, Oranda.Core.Metadata.ObjectDetail detail) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult(detail);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult("CREATE TABLE SAMPLE.ORDERS (…)");

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => Task.FromResult<IReadOnlyList<CompileError>>([]);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>지어낸 검사 읽기 건수(실제 서버 없음).</summary>
    private sealed class ExampleChecks : ITableEditChecks
    {
        public long Overflow { get; init; }

        public long Nulls { get; init; }

        public IReadOnlyList<DbObjectRef> Dependents { get; init; } = [];

        public Dbms Dbms => Dbms.Oracle;

        public Task<long> CountRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(1_240_000L);

        public Task<long?> NumRowsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult<long?>(1_240_000);

        public Task<long> OverflowCountAsync(ISession s, DbObjectRef table, string column, ColumnDraft target, CancellationToken ct) => Task.FromResult(Overflow);

        public Task<long> NullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(Nulls);

        public Task<long> NonNullCountAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) => Task.FromResult(1_240_000L);

        public Task<long> DuplicateKeyCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, CancellationToken ct) => Task.FromResult(0L);

        public Task<long> OrphanCountAsync(ISession s, DbObjectRef table, IReadOnlyList<string> columns, DbObjectRef referencedTable, IReadOnlyList<string> referencedColumns, CancellationToken ct) =>
            Task.FromResult(0L);

        public Task<long> CheckViolationCountAsync(ISession s, DbObjectRef table, string condition, CancellationToken ct) => Task.FromResult(0L);

        public Task<ExpressionPreview> PreviewExpressionAsync(ISession s, DbObjectRef table, string expression, IReadOnlyList<string> columns, int rows, CancellationToken ct) =>
            Task.FromResult(new ExpressionPreview([expression, .. columns], [["1500", "15000"], ["990", "9900"]]));

        public Task<string?> ValidateExpressionAsync(ISession s, string owner, IReadOnlyList<ColumnDraft> columns, string expression, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> NameExistsAsync(ISession s, string owner, string name, TableEditNameKind kind, DbObjectRef? table, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<DbObjectRef>> DependentsAsync(ISession s, DbObjectRef table, CancellationToken ct) => Task.FromResult(Dependents);
    }

    private sealed class ExampleConversions : IColumnConversionChecks
    {
        public Dbms Dbms => Dbms.Oracle;

        public Task<ConversionFailures> FailingRowsAsync(ISession s, DbObjectRef table, ColumnDraft source, ColumnDraft target, string expression, CancellationToken ct) =>
            Task.FromResult(new ConversionFailures(3) { ViewRowsSql = "SELECT * FROM SAMPLE.ORDERS WHERE VALIDATE_CONVERSION(STATUS AS NUMBER) = 0" });

        public Task<IReadOnlyList<ForeignKeyRef>> ReferencingForeignKeysAsync(ISession s, DbObjectRef table, string column, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ForeignKeyRef>>([]);

        public Task<ConversionFailures> ConvertedCheckViolationsAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, string condition, CancellationToken ct) =>
            Task.FromResult(ConversionFailures.None);

        public Task<ConversionFailures> ConvertedDuplicateKeysAsync(
            ISession s, DbObjectRef table, IReadOnlyList<string> otherColumns, string column, string expression, IReadOnlyList<string> keyColumns, CancellationToken ct) =>
            Task.FromResult(ConversionFailures.None);
    }
}
