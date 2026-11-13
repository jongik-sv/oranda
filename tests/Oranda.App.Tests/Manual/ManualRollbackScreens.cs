using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Execution;
using Oranda.App.Features.PointCompare;
using Oranda.App.Features.Rollback;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Core.Text;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 「되돌리기 지점」 장 화면 캡처 생성기(되돌리기 지점 설계 17장): 변경 일지(화면 1), 원복 스크립트 탭(화면 2), 운영 확인 창(화면 4), 설정 「되돌리기」 탭(화면 5).
/// DB 를 부르지 않고 메모리 저장소와 지어낸 예시 값(prod-example·dev-example·SAMPLE.PRODUCTS)만 쓴다. 실행 단추는 누르지 않는다.
/// 화면 3(이 실행 전과 비교)은 지어낸 DB 응답(<see cref="ExampleCompareDb"/>)으로 비교한 결과를 그린다. 반영 단추는 누르지 않는다.
/// 번호의 뜻은 <c>site/src/content/docs/run/rollback.md</c>, <c>settings/options.md</c> 의 번호별 설명과 같아야 한다.
/// 그림은 Windows 에서 같은 시험으로 만든다(Mac 렌더는 커밋하지 않는다).
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualRollbackScreens
{
    private static readonly DateTimeOffset Day = new(2026, 11, 12, 0, 0, 0, TimeSpan.FromHours(9));
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        return window;
    }

    /// <summary>단추 오른쪽 끝에 번호를 얹는다(글자를 가리지 않게).</summary>
    private static ManualCapture.Callout AtRight(Control target, int number) => new(target, number, target.Bounds.Width + 4, 0);

    private static DateTimeOffset At(int hour, int minute, int second = 0) => Day.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private static ChangePoint Scn(long n, DateTimeOffset at) => new(n, "SCN", at, "example");

    private static RollbackStatementInfo Stmt(int seq, DateTimeOffset at, string summary, long rows, int steps, long point) =>
        new(seq, at, DmlVerb.Update, Products, summary, rows, Scn(point, at), RollbackScriptState.Open, null, steps, false);

    /// <summary>시안 화면 1 의 예시: 운영 확정 묶음(문장 3개), 개발 묶음(만들지 않음), 불완전, 롤백.</summary>
    private static async Task<(InMemoryRollbackStore Store, long ProdId)> ExampleStoreAsync()
    {
        var store = new InMemoryRollbackStore();
        var prodProfile = Guid.NewGuid();
        RollbackGroup Group(string profile, ConnectionEnvironment env, DateTimeOffset ended) => new()
        {
            ProfileId = env == ConnectionEnvironment.Production ? prodProfile : Guid.NewGuid(),
            ProfileName = profile,
            DbKey = profile,
            Environment = env,
            StartedAt = ended.AddMinutes(-2),
            EndedAt = ended,
            EndKind = TransactionEndKind.Commit,
            Tables = [Products],
        };

        await store.SaveAsync(Group("prod-example", ConnectionEnvironment.Production, At(9, 30)) with
        {
            EndKind = TransactionEndKind.Rollback,
            State = RollbackScriptState.Invalid,
            BasePoint = Scn(48190311, At(9, 28)),
            RowsTotal = 1240,
            Tables = [new DbObjectRef("SAMPLE", "ORDERS", "TABLE")],
        }, null, default);
        await store.SaveAsync(Group("prod-example", ConnectionEnvironment.Production, At(11, 20, 47)) with
        {
            State = RollbackScriptState.Incomplete,
            StateReason = "INSERT … SELECT",
            BasePoint = Scn(48195502, At(11, 20)),
            CommitPoint = Scn(48195530, At(11, 20, 47)),
            RowsTotal = 87,
            StepCount = 0,
            Tables = [new DbObjectRef("SAMPLE", "ORDER_ITEMS", "TABLE")],
        }, new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0, []), default);
        var prodId = await store.SaveAsync(Group("prod-example", ConnectionEnvironment.Production, At(14, 5, 10)) with
        {
            State = RollbackScriptState.Confirmed,
            BasePoint = Scn(48213907, At(14, 3, 21)),
            CommitPoint = Scn(48214022, At(14, 5, 10)),
            RowsTotal = 16,
            StepCount = 16,
            Statements =
            [
                Stmt(1, At(14, 3, 21), "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3", 12, 12, 48213907),
                Stmt(2, At(14, 4, 2), "DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'", 3, 3, 48213954) with { Verb = DmlVerb.Delete },
                Stmt(3, At(14, 4, 40), "INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, …) VALUES (2001, …)", 1, 1, 48213991) with { Verb = DmlVerb.InsertValues },
            ],
        }, ExampleDocument(), default);
        await store.SaveAsync(Group("dev-example", ConnectionEnvironment.Development, At(15, 14, 2)) with
        {
            State = RollbackScriptState.NotCreated,
            Reason = NotCreatedReason.NotProduction,
            BasePoint = Scn(48220114, At(15, 12, 40)),
            CommitPoint = Scn(48220231, At(15, 14, 2)),
            RowsTotal = 9,
        }, null, default);
        return (store, prodId);
    }

    /// <summary>시안 화면 2 의 원복 스크립트 예시(설계 6.7).</summary>
    private static RollbackScriptDocument ExampleDocument() => new(RollbackScriptDocument.CurrentSchemaVersion, 0,
    [
        new(1, At(14, 3, 21), "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3", [],
        [
            // 엔진이 만드는 원복 문장은 한 줄이다. 줄 바꾸기는 RollbackScriptWriter.Format 이 한다(여기서 바꾸면 빈 줄이 생긴다).
            new("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 25900 WHERE PRODUCT_ID = 1001 AND UNIT_PRICE = 28490"),
            new("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730"),
        ], [], false, null, 12),
        new(2, At(14, 4, 2), "DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'", [],
        [
            new("INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO) VALUES (1040, '보리차 티백', 3, 4730, 'Y', NULL)"),
            new("INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO) VALUES (1041, '현미 녹차', 3, 5610, 'Y', '단종 예정')"),
        ], [], false, null, 3),
        new(3, At(14, 4, 40), "INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME, CATEGORY_ID, UNIT_PRICE, DISCONTINUED, MEMO) VALUES (2001, '유자차 스틱', 3, 6200, 'N', NULL)", [],
        [
            new("DELETE FROM SAMPLE.PRODUCTS WHERE PRODUCT_ID = 2001 AND PRODUCT_NAME = '유자차 스틱' AND CATEGORY_ID = 3 AND UNIT_PRICE = 6200 AND DISCONTINUED = 'N' AND MEMO IS NULL"),
        ], [], false, null, 1),
    ]);

    [Fact]
    public Task Rollback_journal() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (store, prodId) = await ExampleStoreAsync();
        var journal = new ExampleJournal(new JournalEntry
        {
            Id = 1,
            CapturedAt = At(8, 30),
            ProfileName = "dev-example",
            DbKey = "dev-example",
            Owner = "SAMPLE",
            Name = "EMPLOYEES",
            ObjectType = "TABLE",
            Cause = JournalCause.Ddl,
            StatementSummary = "ALTER TABLE EMPLOYEES ADD JOB_TITLE VARCHAR2(30)",
            PreviousDdl = "CREATE TABLE SAMPLE.EMPLOYEES (EMP_ID NUMBER(6) NOT NULL);",
        });
        using var vm = new ChangeJournalViewModel(journal, store) { RetentionDaysProvider = () => 30 };
        await vm.ReloadAsync();
        var view = new ChangeJournalView { DataContext = vm };
        var window = Show(view, 1280, 560);
        var overlay = ManualCapture.AddOverlay(window); // 창에 붙을 때 목록을 다시 읽으므로 먼저 얹는다
        await vm.ReloadAsync();
        var group = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == prodId);
        vm.ToggleExpand(group);
        Pump();
        Named<ListBox>(view, "List").SelectedItem = group;
        await HeadlessHost.WaitUntil(() => vm.SelectedGroup is not null);
        Pump();

        ManualCapture.SaveOverlaid(window, overlay, "rollback-journal",
        [
            AtRight(Named<ComboBox>(view, "CauseBox"), 1),
            AtRight(Named<Button>(view, "OpenScriptButton"), 2),
            AtRight(Named<Button>(view, "CompareButton"), 3),
            new(Named<ListBox>(view, "List"), 4, 4, 30),
        ]);
        window.Close();
    });

    [Fact]
    public Task Rollback_script() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var group = new RollbackGroup
        {
            Id = 318,
            ProfileId = Guid.NewGuid(),
            ProfileName = "prod-example",
            DbKey = "prod-example",
            Environment = ConnectionEnvironment.Production,
            StartedAt = At(14, 3, 21),
            EndedAt = At(14, 5, 10),
            EndKind = TransactionEndKind.Commit,
            CommitPoint = Scn(48214022, At(14, 5, 10)),
            ExpiresAt = At(14, 5, 10).AddDays(30),
            State = RollbackScriptState.Confirmed,
            HasScriptFile = true,
        };
        var vm = new RollbackScriptViewModel(group, ExampleDocument());
        var view = new RollbackScriptView { DataContext = vm };
        var window = Show(view, 1100, 560);
        ManualCapture.Save(window, "rollback-script",
        [
            AtRight(Named<Button>(view, "RunButton"), 1),
            AtRight(Named<Button>(view, "CopyButton"), 2),
            AtRight(Named<Button>(view, "ExportButton"), 3),
            new(Named<Control>(view, "ScriptBox"), 4, 900, 40),
            new(Named<TextBlock>(view, "StatusText"), 5, 0, -14),
        ]);
        window.Close();
    });

    [Fact]
    public Task Rollback_confirm() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        const string Sql = "UPDATE SAMPLE.ORDERS SET STATUS = 'CLOSED'\n WHERE ORDER_DATE < DATE '2026-10-01'";
        var request = new ConfirmRequest(
            new SqlStatement(Sql, new TextRange(0, Sql.Length), StatementKind.Dml),
            new PolicyDecision(PolicyDecisionKind.Confirm, PolicyRule.ProductionChange, "prod-example (운영) 에서 UPDATE 를 실행합니다."),
            new ConnectionProfile("prod-example", "app", "db.example:1521/SAMPLE", Environment: ConnectionEnvironment.Production))
        {
            Extras = new ConfirmExtras(new RollbackOffer(true, NotCreatedReason.None)
            {
                EstimatedRows = 1240,
                DefaultOn = true,
                Warnings = ["대상 테이블에 트리거 1개가 있습니다. 트리거가 바꾼 것은 원복 스크립트에 들어가지 않습니다."],
            }, null),
        };
        var (window, _) = AvaloniaExecutionDialogs.CreateConfirmDialog(new ConfirmDialogModel(request));
        window.Show();
        Pump();
        window.UpdateLayout();
        Pump();
        ManualCapture.Save(window, "rollback-confirm",
        [
            new(Named<CheckBox>(window, "ConfirmRollbackBox"), 1, -14),
            new(ManualCapture.FindByText(window, "같은 조건으로 먼저 잠그고 읽으므로 실행 시간이 늘어납니다. 이 PC 에 암호화해 30일 보관합니다."), 2, -14),
            new(ManualCapture.FindByText(window, "대상 테이블에 트리거 1개가 있습니다. 트리거가 바꾼 것은 원복 스크립트에 들어가지 않습니다."), 3, -14),
            new(ManualCapture.FindByText(window, "실행 직전 SCN 은 원복 스크립트와 상관없이 기록합니다."), 4, -14),
        ]);
        window.Close();
    });

    [Fact]
    public Task Settings_rollback() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var (store, _) = await ExampleStoreAsync();
        var data = Path.Combine(Path.GetTempPath(), "oranda-manual-rb-" + Guid.NewGuid().ToString("N"));
        var month = Directory.CreateDirectory(Path.Combine(data, "rollback", "2026-11"));
        try
        {
            await using (var file = File.Create(Path.Combine(month.FullName, "00000003.orb")))
            {
                file.SetLength(3_250_000); // 「3.1 MB」
            }

            var settings = new SettingsService(new MemorySettingsStore());
            var text = new ShellText(language: "ko");
            var page = new RollbackSettingsPage(settings.Current.Rollback, text, store, new Oranda.Platform.Storage.AppDataPaths(data));
            await page.RefreshSummaryAsync();
            var vm = new SettingsViewModel(settings, text, rollback: page) { SelectedTab = SettingsViewModel.RollbackTabIndex };
            var window = new SettingsWindow { DataContext = vm };
            window.Show();
            for (var i = 0; i < 4; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(20); // 탭 전환 애니메이션을 끝낸다
                Pump();
                window.UpdateLayout();
            }

            ManualCapture.Save(window, "settings-rollback",
            [
                new(Named<CheckBox>(window, "RollbackRecordPointBox"), 1, -14),
                new(Named<CheckBox>(window, "RollbackScriptBox"), 2, -14),
                new(Named<NumericUpDown>(window, "RollbackMaxRowsBox"), 3, -14),
                new(Named<NumericUpDown>(window, "RollbackRetentionBox"), 4, -14),
                new(Named<CheckBox>(window, "RollbackWarnMismatchBox"), 5, -14),
                new(Named<CheckBox>(window, "RollbackBackupBox"), 6, -14),
                new(Named<Button>(window, "RollbackClearButton"), 7, -14),
            ]);
            window.Close();
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    });

    [Fact]
    public Task Rollback_compare() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        var point = new PointOption("기준 SCN 48220114 (실행 직전)", PointOptionKind.Group)
        {
            BasePoint = 48220114,
            BaseAt = DateTimeOffset.Now.AddMinutes(-13),
            CommitPoint = 48220231,
            CommitAt = DateTimeOffset.Now.AddMinutes(-11),
            GroupId = 321,
            Tables = [Products],
        };
        var vm = new PointCompareViewModel(new ExampleCompareDb(), "dev-example", Products, [point]);
        var window = new PointCompareWindow { DataContext = vm, Width = 1100, Height = 780 };
        window.Show();
        await vm.LoadAsync();
        Pump();
        window.UpdateLayout();
        Pump();

        ManualCapture.Save(window, "rollback-compare",
        [
            new(Named<ComboBox>(window, "PointBox"), 1, -14),
            new(Named<TextBlock>(window, "NotesText"), 2, -22),
            new(Named<TextBlock>(window, "CountsLine"), 3, -22),
            new(Named<ListBox>(window, "RowList"), 4, -14),
            new(Named<Control>(window, "DmlView"), 5, -14),
            AtRight(Named<Button>(window, "ApplyButton"), 6),
        ]);
        window.Close();
    });

    /// <summary>
    /// 시안 화면 3 의 예시 응답(SAMPLE.PRODUCTS): 1001·1002 되돌림, 1007 충돌(그 뒤 다른 세션), 1040 되살림, 2001 지움. 쓰기는 하지 않는다.
    /// 질의 글로 기준(48220114)·커밋(48220231)·지금 값을 가른다.
    /// </summary>
    private sealed class ExampleCompareDb : IPointCompareDb
    {
        private static readonly RollbackTable Table = new(Products,
            [
                new RollbackColumn("PRODUCT_ID", "NUMBER", RollbackValueKind.Number),
                new RollbackColumn("PRODUCT_NAME", "VARCHAR2", RollbackValueKind.Text),
                new RollbackColumn("UNIT_PRICE", "NUMBER", RollbackValueKind.Number),
            ],
            ["PRODUCT_ID"]);

        private readonly FakeRollbackDialect _dialect = new();

        public Dbms Dbms => _dialect.Dbms;

        public IRollbackDialect Dialect => _dialect;

        private static object?[] Row(int id, string name, int price) =>
            [id.ToString(System.Globalization.CultureInfo.InvariantCulture), name, price.ToString(System.Globalization.CultureInfo.InvariantCulture), "AAAR" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)];

        public Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<RollbackTable?>(Table);

        public Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct)
        {
            var sql = query.Sql;
            IReadOnlyList<object?[]> rows = sql switch
            {
                _ when sql.Contains("VERSIONS BETWEEN", StringComparison.Ordinal) => [["1001"], ["1002"], ["1007"], ["1040"], ["2001"]],
                _ when sql.Contains("COUNT(*)", StringComparison.Ordinal) => [[5m]],
                _ when sql.Contains(" 48220114 t", StringComparison.Ordinal) =>
                    [Row(1001, "얼그레이 티백", 25900), Row(1002, "보이차", 8000), Row(1007, "현미 녹차", 19000), Row(1040, "보리차 티백", 4300)],
                _ when sql.Contains(" 48220231 t", StringComparison.Ordinal) =>
                    [Row(1001, "얼그레이 티백", 28490), Row(1002, "보이차", 8800), Row(1007, "현미 녹차", 20900), Row(2001, "유자차 스틱", 6200)],
                _ => [Row(1001, "얼그레이 티백", 28490), Row(1002, "보이차", 8800), Row(1007, "현미 녹차", 21500), Row(2001, "유자차 스틱", 6200)],
            };
            return Task.FromResult(rows);
        }

        public Task<long?> CurrentPointAsync(CancellationToken ct) => Task.FromResult<long?>(48230000);

        public Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct) => Task.FromResult<long?>(48220114);

        public Task<long?> UndoRetentionAsync(CancellationToken ct) => Task.FromResult<long?>(900);

        public Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task<long> ExecuteAsync(string sql, CancellationToken ct) => throw new InvalidOperationException("매뉴얼 그림 시험은 반영하지 않는다.");
    }

    private sealed class ExampleJournal(params JournalEntry[] all) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<JournalEntry>>(all);

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult(all.FirstOrDefault(e => e.Id == id));

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }
}
