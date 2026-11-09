using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.LockMonitor;
using Oranda.App.Features.SessionMonitor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.SqlMonitor;
using Oranda.App.Features.TableSpec;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.DataTransfer;
using Oranda.App.Tests.Features.LockMonitor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Journal;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 모니터·DB 도구·내보내기/가져오기 장 화면 캡처 생성기.
/// 모니터 세 화면은 샘플 DB 에 대기와 부하가 없고 잠금을 일부러 만들지도 않으므로 지어낸 예시 값(<see cref="ExampleMonitor"/>)으로 그린다. 세션 종료·트레이스는 부르지 않는다.
/// DB 정보·DDL 일괄 추출·테이블 정의서·가져오기는 샘플 DB 를 읽기만 한다. 가져오기 단추는 누르지 않는다.
/// 번호의 뜻은 <c>tools/monitor.md</c>, <c>tools/db-tools.md</c>, <c>transfer/data-transfer.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualToolScreens
{
    /// <summary>화면에 경로가 보이므로 임시 폴더 이름이나 PC 사용자 이름이 나오지 않는 고정 예시 폴더를 쓴다.</summary>
    private static readonly string ExampleFolder = Path.GetFullPath("/tmp/acme-hr");

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static T Up<T>(Visual from)
        where T : Visual =>
        from.FindAncestorOfType<T>() ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name} 조상을 찾지 못했다.");

    private static T ByAutomationName<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => AutomationProperties.GetName(c) == name);

    private static Window Show(Control view, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        return window;
    }

    private static FakeLockSession ExampleSession() => new(
        new ServerInfo("23.26.0.0.0", "FREE", "db-example", "FREEPDB1", "FREE", "FREEPDB1", true, 1, LockHarness.Dba),
        new ConnectionProfile("dev-example", "app_dev", "db.example.com:1521/devsvc", Environment: ConnectionEnvironment.Development));

    // ---- 모니터 --------------------------------------------------------------------------------

    [Fact]
    public Task Session_monitor() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var session = ExampleSession();
        var vm = new SessionMonitorViewModel(new ExampleMonitor(), _ => Task.FromResult<ISession?>(session), new ContextKeyService()) { PostToUi = a => a() };
        await vm.StartAsync();
        await vm.RefreshAsync();
        vm.SelectedSession = vm.Sessions[0];
        var view = new SessionMonitorView { DataContext = vm };
        var window = Show(view, 1280, 620);
        var grid = view.FindControl<MonitorGrid>("SessionList")!;
        await HeadlessHost.WaitUntil(() => grid.Model is { RowCount: > 0 } && grid.GridControl.Bounds.Height > 0);
        Pump();

        ManualCapture.Save(window, "monitor-session",
        [
            new(Up<WrapPanel>(Named<Button>(view, "RefreshButton")), 1, 1215, 18),
            new(ByAutomationName<TextBox>(view, "사용자 거르기"), 2, 565, 16),
            new(grid, 3, 660, 12),
            new(ManualCapture.Find<TabControl>(view), 4),
        ]);
        window.Close();
    });

    [Fact]
    public Task Sql_monitor() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var session = ExampleSession();
        var vm = new SqlMonitorViewModel(new ExampleMonitor(), null, _ => Task.FromResult<ISession?>(session), new ContextKeyService()) { PostToUi = a => a() };
        await vm.StartAsync();
        await vm.SearchAsync();
        vm.SelectedSql = vm.Results[1];
        await vm.ShowBindsAsync();
        var view = new SqlMonitorView { DataContext = vm };
        var window = Show(view, 1280, 680);
        var grid = view.FindControl<MonitorGrid>("ResultList")!;
        await HeadlessHost.WaitUntil(() => grid.Model is { RowCount: > 0 } && grid.GridControl.Bounds.Height > 0);
        Pump();

        ManualCapture.Save(window, "monitor-sql",
        [
            new(Named<ComboBox>(view, "PresetBox"), 1, 120, 14),
            new(ManualCapture.Find<TextBox>(view, t => t.PlaceholderText == "SQL 텍스트·SQL ID"), 2, 772, 16),
            new(grid, 3, 1180, 12),
            new(Named<TabControl>(view, "DetailTabs"), 4, 575, 14),
        ]);
        window.Close();
    });

    [Fact]
    public Task Lock_monitor() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var session = ExampleSession();
        var vm = new LockMonitorViewModel(new ExampleMonitor(), _ => Task.FromResult<ISession?>(session), new ContextKeyService())
        {
            PostToUi = a => a(),
            StartAutoRefresh = false,
            AlertEnabled = false, // 알림 창이 오른쪽 탭을 가리지 않게 한다
        };
        await vm.StartAsync();
        var start = ExampleMonitor.Now.AddMinutes(-30);
        for (var i = 0; i < 355; i++)
        {
            var waiters = i < 250 ? (i % 60 < 6 ? 1 : 0) : 2;
            var wait = i < 250 ? (i % 60 < 6 ? 15L : 0L) : (i - 250) * 3L;
            vm.History!.Add(new LockHistoryPoint(start.AddSeconds(i * 5), waiters, wait));
        }

        await vm.RefreshKeepingDetailAsync();

        var view = new LockMonitorView { DataContext = vm };
        var window = Show(view, 1280, 720);
        view.Rebuild();
        Pump();
        vm.SelectedKey = ExampleMonitor.Waiter;
        Pump();
        window.UpdateLayout();
        Pump();

        ManualCapture.Save(window, "monitor-lock",
        [
            new(Up<Border>(Named<Button>(view, "RefreshButton")), 1, 1120, 18),
            new(Named<TabControl>(view, "TopTabs"), 2, 475, 14),
            new(Named<TabControl>(view, "DetailTabs"), 3, 275, 14),
            new(Named<Border>(view, "HistoryStrip"), 4),
        ]);
        window.Close();
    });

    // ---- DB 도구 --------------------------------------------------------------------------------

    [Fact]
    public Task Db_info() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await ManualShell.OpenAsync();
        var vm = new DbInfoViewModel(shell.Harness.Services);
        await vm.LoadAsync(SessionRouter.CurrentOf(shell.Harness.Services)!);
        vm.SelectedSection = vm.Sections.FirstOrDefault(s => s.Title == "파라미터") ?? vm.Sections[0];
        vm.PickParameter("processes", "300");
        vm.ParameterValue = "400";
        var view = new DbInfoView { DataContext = vm };
        var window = Show(view, 1100, 620);

        ManualCapture.Save(window, "db-info",
        [
            new(Named<ListBox>(view, "SectionList"), 1, 112, 14),
            new(view.FindControl<Control>("Table")!, 2, 500, 12),
            new(ByAutomationName<TextBox>(view, "이 구역에서 찾기"), 3, 254, 14),
            new(ManualCapture.FindByText(view, "파라미터 변경 DDL 미리보기 (실행하지 않습니다)"), 4, 292),
        ]);
        window.Close();
    });

    [Fact]
    public Task Ddl_extract() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await ManualShell.OpenAsync();
        var vm = new DdlExtractViewModel(shell.Harness.Services);
        vm.Attach(SessionRouter.CurrentOf(shell.Harness.Services)!);
        vm.OutputDirectory = Path.Combine(ExampleFolder, "ddl");
        await vm.RefreshTargetsAsync();
        var view = new DdlExtractView { DataContext = vm };
        var window = Show(view, 1200, 830);
        await HeadlessHost.WaitUntil(() => vm.Targets.Count > 0, 30000);
        Pump();

        ManualCapture.Save(window, "ddl-extract",
        [
            new(ManualCapture.FindByText(view, "1. 대상"), 1, 62),
            new(ManualCapture.FindByText(view, "2. 저장 속성"), 2, 88),
            new(ManualCapture.FindByText(view, "3. 이름 바꾸기 (한 줄에 이전=새)"), 3, 200),
            new(ManualCapture.FindByText(view, "4. 출력"), 4, 62),
            new(Named<TabControl>(view, "RightTabs"), 5, 150, 14),
        ]);
        window.Close();
    });

    [Fact]
    public Task Table_spec() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await ManualShell.OpenAsync();
        var vm = new TableSpecViewModel(shell.Harness.Services);
        vm.Attach(SessionRouter.CurrentOf(shell.Harness.Services)!);
        var folder = Path.Combine(ExampleFolder, "spec");
        Directory.CreateDirectory(folder);
        try
        {
            Assert.True(await vm.GenerateAsync(Path.Combine(folder, vm.Owner + "_테이블정의서.xlsx")), vm.StatusText);
            var view = new TableSpecView { DataContext = vm };
            var window = Show(view, 1000, 460);
            ManualCapture.Save(window, "table-spec",
            [
                new(ManualCapture.FindByText(view, "1. 대상"), 1, 62),
                new(ManualCapture.FindByText(view, "2. 형식"), 2, 62),
                new(ManualCapture.FindByText(view, "3. 저장"), 3, 62),
            ]);
            window.Close();
        }
        finally
        {
            Directory.Delete(ExampleFolder, recursive: true);
        }
    });

    [Fact]
    public Task Change_journal() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        using var shell = ManualShell.OpenOffline();
        var store = shell.Harness.Services.GetService<IChangeJournalStore>()!;
        var profileId = Guid.NewGuid();
        JournalEntry Entry(int day, string name, string type, JournalCause cause, string summary, string ddl) => new()
        {
            CapturedAt = new DateTimeOffset(2026, 10, day, 9, 30, 0, TimeSpan.FromHours(9)),
            ProfileId = profileId,
            ProfileName = "dev-example",
            DbKey = "EXAMPLE/SAMPLE",
            Owner = "SAMPLE",
            Name = name,
            ObjectType = type,
            Cause = cause,
            StatementSummary = summary,
            PreviousDdl = ddl,
            ContentHash = ddl.GetHashCode(StringComparison.Ordinal).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        const string First = "CREATE TABLE SAMPLE.EMPLOYEES (\n  EMP_ID NUMBER(6) NOT NULL,\n  EMP_NAME VARCHAR2(40) NOT NULL,\n  SALARY NUMBER(9) NOT NULL\n);";
        const string Second = "CREATE TABLE SAMPLE.EMPLOYEES (\n  EMP_ID NUMBER(6) NOT NULL,\n  EMP_NAME VARCHAR2(40) NOT NULL,\n  SALARY NUMBER(9) NOT NULL,\n  EMAIL VARCHAR2(60)\n);";
        await store.AddAsync(Entry(20, "EMPLOYEES", "TABLE", JournalCause.Ddl, "ALTER TABLE EMPLOYEES ADD EMAIL VARCHAR2(60)", First), default);
        await store.AddAsync(Entry(22, "ORDER_PKG", "PACKAGE BODY", JournalCause.Compile, "CREATE OR REPLACE PACKAGE BODY ORDER_PKG", "CREATE OR REPLACE PACKAGE BODY SAMPLE.ORDER_PKG AS\n  -- 이전 소스\nEND;"), default);
        await store.AddAsync(Entry(24, "EMPLOYEES", "TABLE", JournalCause.Ddl, "ALTER TABLE EMPLOYEES ADD JOB_TITLE VARCHAR2(30)", Second), default);

        using var vm = new ChangeJournalViewModel(shell.Harness.Services);
        await vm.ReloadAsync();
        var view = new ChangeJournalView { DataContext = vm };
        var window = Show(view, 1180, 640);
        var overlay = ManualCapture.AddOverlay(window); // 창에 붙을 때 목록을 다시 읽어 선택이 풀리므로 먼저 얹는다
        await vm.ReloadAsync();
        Pump();
        Named<ListBox>(view, "List").SelectedItem = vm.Items.First(i => i.Entry.Name == "EMPLOYEES");
        await HeadlessHost.WaitUntil(() => vm.Selection.Count > 0 && vm.Diff.Count > 0);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "change-journal",
        [
            new(Named<TextBox>(view, "SearchBox"), 1, 485, 14),
            new(Named<ListBox>(view, "List"), 2, 1125, 12),
            new(Named<Control>(view, "DetailBox"), 3, 360, 12),
            new(Named<Control>(view, "DiffList"), 4, 360, 12),
        ]);
        window.Close();
    });

    // ---- 내보내기·가져오기 ------------------------------------------------------------------------

    [Fact]
    public Task Export_dialog() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var folder = Path.Combine(ExampleFolder, "export");
        Directory.CreateDirectory(folder);
        var oldFolder = ExcelExportPlan.LastFolder;
        ExcelExportPlan.LastFolder = folder;
        try
        {
            await using var grid = await ExportDialogTests.GridAsync(30);
            var vm = new ExportDialogViewModel(
                new ExportDialogTests.RecordingTransfer(), ExportDialogTests.Source("EMPLOYEES"), grid, TransferFormat.Xlsx, openAfter: true, now: new DateTime(2026, 11, 4, 15, 30, 0));
            var window = new ExportDialogWindow { DataContext = vm };
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            ManualCapture.Save(window, "export-dialog",
            [
                new(Named<ComboBox>(window, "FormatBox"), 1),
                new(Named<RadioButton>(window, "AllRowsRadio"), 2),
                new(Named<TextBox>(window, "PathBox"), 3),
                new(Named<CheckBox>(window, "OpenAfterBox"), 4),
                new(Named<Expander>(window, "AdvancedExpander"), 5),
                new(Named<Button>(window, "StartButton"), 6),
            ]);
            window.Close();
        }
        finally
        {
            ExcelExportPlan.LastFolder = oldFolder;
            Directory.Delete(ExampleFolder, recursive: true);
        }
    });

    [Fact]
    public Task Import_dialog() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var folder = Path.Combine(ExampleFolder, "import");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "employees.csv");
        await File.WriteAllTextAsync(file, "EMP_ID,EMP_NAME,DEPT_ID,JOB_TITLE,SALARY,HIRE_DATE,EMAIL,비고\n2001,김하늘,10,개발자,5200000,2026-03-02,sky@example.com,신규\n2002,이바다,20,분석가,4800000,2026-03-09,sea@example.com,신규\n");
        try
        {
            using var shell = await ManualShell.OpenAsync();
            var services = shell.Harness.Services;
            var vm = new ImportDialogViewModel(TransferServices.Resolve(services)!, TransferServices.ResolveMetadata(services)!, SessionRouter.CurrentOf(services)!,
                Oranda.App.Features.DbmsServices.For<Oranda.Core.Transfer.IImportValueConverter>(services, SessionRouter.CurrentOf(services)))
            {
                FilePath = file,
                TargetOwner = "SAMPLE",
                TargetTable = "EMPLOYEES",
            };
            await vm.LoadAsync(); // 파일 앞부분과 표의 열 정보만 읽는다. 가져오기는 시작하지 않는다.
            var window = new ImportDialogWindow { DataContext = vm };
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            ManualCapture.Save(window, "import-dialog",
            [
                new(ByAutomationName<TextBox>(window, "가져올 파일 경로"), 1),
                new(ByAutomationName<TextBox>(window, "대상 스키마"), 2),
                new(ManualCapture.Find<CheckBox>(window, c => c.Content as string == "첫 줄은 열 이름"), 3),
                new(ByAutomationName<ListBox>(window, "열 매핑"), 4, 660, 14),
                new(ByAutomationName<ListBox>(window, "원본 앞부분"), 5, 660, 14),
                new(ManualCapture.Find<Button>(window, b => b.Content as string == "가져오기"), 6),
            ]);
            window.Close();
        }
        finally
        {
            Directory.Delete(ExampleFolder, recursive: true);
        }
    });

    /// <summary>모니터 화면용 지어낸 값. 조회만 답하고 세션 종료·트레이스는 부르면 시험을 실패시킨다.</summary>
    private sealed class ExampleMonitor : IMonitorService
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 4, 14, 7, 23, TimeSpan.FromHours(9));

        public static readonly SessionKey Holder = new(1, 1287, 40211);

        public static readonly SessionKey Waiter = new(1, 1422, 9001);

        private static readonly SessionKey Waiter2 = new(1, 388, 9002);

        private const string HolderSql = "UPDATE orders SET status = 'SHIPPED' WHERE order_id = :1";

        private const string WaiterSql = "UPDATE orders SET total_amount = :1 WHERE order_id = :2";

        private static SessionRow Row(SessionKey key, string status, string user, string program, long elapsed, string? sqlId = null, string? wait = null, SessionKey? blocker = null) => new(key, status)
        {
            Username = user,
            OsUser = "app",
            Machine = "app-server-01",
            Program = program,
            Module = program,
            SqlId = sqlId,
            Event = wait,
            WaitClass = wait is null ? null : "Application",
            LastCallElapsedSeconds = elapsed,
            BlockingSession = blocker,
        };

        public Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionRow>>(
        [
            Row(Holder, "INACTIVE", "APP_USER", "JDBC Thin Client", 312),
            Row(Waiter, "ACTIVE", "APP_USER", "JDBC Thin Client", 287, "7h35uxf5uhmm1", "enq: TX - row lock contention", Holder),
            Row(Waiter2, "ACTIVE", "RPT_USER", "Oranda", 141, "7h35uxf5uhmm1", "enq: TX - row lock contention", Holder),
            Row(new SessionKey(1, 512, 3301), "ACTIVE", "BATCH_USER", "batch-runner", 18, "9zq0m3r1kx2pa", "db file sequential read"),
            Row(new SessionKey(1, 644, 7001), "INACTIVE", "SAMPLE", "Oranda", 75),
            Row(new SessionKey(1, 911, 2104), "INACTIVE", "RPT_USER", "Oranda", 960),
        ]);

        public Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct) => Task.FromResult(Tree());

        public Task<SessionDetail> GetSessionDetailAsync(ISession monitor, SessionKey target, CancellationToken ct) =>
            Task.FromResult(new SessionDetail(new SessionRow(target, "ACTIVE") { Username = "APP_USER" }) { CurrentSqlText = WaiterSql });

        public Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct) =>
            throw new InvalidOperationException("매뉴얼 캡처는 세션 종료를 하지 않는다.");

        public Task<string?> SetTraceAsync(ISession monitor, SessionKey target, bool enable, TraceOptions options, CancellationToken ct) =>
            throw new InvalidOperationException("매뉴얼 캡처는 트레이스를 켜지 않는다.");

        public Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct)
        {
            TopSqlRow Sql(string id, string text, long runs, double elapsed, double cpu, long gets, long disk, string schema) =>
                new(new CursorRef(id, 0), text) { Executions = runs, ElapsedSeconds = elapsed, CpuSeconds = cpu, BufferGets = gets, DiskReads = disk, ParsingSchema = schema };
            return Task.FromResult<IReadOnlyList<TopSqlRow>>(
            [
                Sql("9zq0m3r1kx2pa", "SELECT c.region, SUM(o.total_amount) FROM orders o JOIN customers c ON c.customer_id = o.customer_id GROUP BY c.region", 42, 186.4, 171.2, 9_120_455, 18_204, "SAMPLE"),
                Sql("7h35uxf5uhmm1", WaiterSql, 1_204, 92.7, 11.3, 361_220, 412, "APP_USER"),
                Sql("3k1d0a7q2m9vz", "SELECT e.emp_name, d.dept_name FROM employees e JOIN departments d ON d.dept_id = e.dept_id WHERE e.salary > :1", 8_930, 41.5, 38.9, 2_480_112, 96, "SAMPLE"),
                Sql("6p2x9w0e1r8tn", "INSERT INTO order_items (order_id, item_no, quantity) VALUES (:1, :2, :3)", 25_310, 12.2, 8.1, 148_022, 37, "APP_USER"),
            ]);
        }

        public Task<SqlDetail> GetSqlDetailAsync(ISession monitor, CursorRef cursor, CancellationToken ct) => Task.FromResult(new SqlDetail(cursor, cursor.SqlId == "4w1xq9n2b7c0d" ? HolderSql : WaiterSql)
        {
            Binds = [new CapturedBind(":1", 1, "NUMBER", "125000"), new CapturedBind(":2", 2, "NUMBER", "1042")],
        });

        public Task<LockSnapshot> GetLocksAsync(ISession monitor, LockFilter filter, CancellationToken ct)
        {
            LockRow R(SessionKey s, string user, string type, int held, int req, long ctime, bool block, long id1, long id2, string? obj = null, string status = "ACTIVE", string? prev = null) => new(s, type)
            {
                Username = user,
                HeldMode = held,
                RequestedMode = req,
                CtimeSeconds = ctime,
                IsBlocking = block,
                Id1 = id1,
                Id2 = id2,
                ObjectName = obj,
                Status = status,
                PrevSqlId = prev,
                Program = "JDBC Thin Client",
                Machine = "app-server-01",
            };
            return Task.FromResult(new LockSnapshot(
            [
                R(Holder, "APP_USER", "TX", 6, 0, 312, true, 327706, 258466, status: "INACTIVE", prev: "4w1xq9n2b7c0d"),
                R(Holder, "APP_USER", "TM", 3, 0, 312, false, 92458, 0, "SAMPLE.ORDERS", "INACTIVE"),
                R(Waiter, "APP_USER", "TX", 0, 6, 287, false, 327706, 258466),
                R(Waiter2, "RPT_USER", "TX", 0, 6, 141, false, 327706, 258466),
            ], Tree(), Now));
        }

        public Task<IReadOnlyList<BlockerImpact>> GetBlockerImpactAsync(ISession monitor, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BlockerImpact>>([new(Holder, 2, 287, 428) { Status = "INACTIVE" }]);

        private static BlockingTree Tree() => new(
        [
            new BlockingNode(Holder,
            [
                new BlockingNode(Waiter, []) { Username = "APP_USER", Event = "enq: TX - row lock contention", SecondsInWait = 287, SqlId = "7h35uxf5uhmm1", LockedObject = "SAMPLE.ORDERS", Status = "ACTIVE" },
                new BlockingNode(Waiter2, []) { Username = "RPT_USER", Event = "enq: TX - row lock contention", SecondsInWait = 141, SqlId = "7h35uxf5uhmm1", LockedObject = "SAMPLE.ORDERS", Status = "ACTIVE" },
            ]) { Username = "APP_USER", Status = "INACTIVE" },
        ]);
    }
}
