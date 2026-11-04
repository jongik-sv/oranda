using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 MyBatis 장 화면 캡처 생성기. 지어낸 매퍼(<see cref="MapperSamples.EmpMapper"/>)만 쓰고 DB 에 접속하지 않는다. 저장 창은 그린 뒤 취소하므로 파일을 쓰지 않는다.
/// 번호의 뜻은 <c>site/src/content/docs/editor/mybatis.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualMyBatisScreens
{
    private const string List = "selectEmpList";
    private const string Update = "updateEmpSal";

    /// <summary>화면에 경로가 보이므로 임시 폴더 이름이나 PC 사용자 이름이 나오지 않는 고정 예시 폴더를 쓴다.</summary>
    private static readonly string ExampleFolder = Path.GetFullPath("/tmp/acme-hr");

    private static string MapperFolder => Path.Combine(ExampleFolder, "mapper");

    private static async Task WithMapperAsync(Func<MyBatisFlowRig, string, Task> body)
    {
        Directory.CreateDirectory(MapperFolder);
        var path = Path.Combine(MapperFolder, "EmpMapper.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        try
        {
            using var rig = MyBatisFlowRig.Create();
            await body(rig, path);
        }
        finally
        {
            Directory.Delete(ExampleFolder, recursive: true);
        }
    }

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

    [Fact]
    public Task Statements() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        await WithMapperAsync(async (rig, path) =>
        {
            var (_, session) = await rig.OpenAsync(path);
            session.OpenStatement(List);
            var tab = session.OpenStatement(Update)!;
            var at = tab.Document.Text.IndexOf("WHERE EMPNO", StringComparison.Ordinal);
            tab.Document.Replace(at, 0, "-- 급여 변경\n ");
            session.SelectStatement(List);
            Pump();

            var vm = new MyBatisStatementsViewModel { Host = rig.Host, Text = new ShellText(language: "ko"), FilterDebounceMilliseconds = 0 };
            var view = new MyBatisStatementsView { DataContext = vm };
            var window = new Window { Width = 460, Height = 620, Content = view };
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            ManualCapture.Save(window, "mybatis-statements",
            [
                new(Named<Border>(view, "HeaderBorder"), 1, 190, 16),
                new(Named<TextBox>(view, "SearchBox"), 2, 420, 14),
                new(Named<ListBox>(view, "Tree"), 3, 380, 12),
                new(Up<StackPanel>(Named<Button>(view, "NewButton")), 4, 170, 12),
                new(Named<StackPanel>(view, "StateFilters"), 5, 110, 12),
            ]);
            window.Close();
        });
    });

    [Fact]
    public Task Statement_tab() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        await WithMapperAsync(async (rig, path) =>
        {
            var (_, session) = await rig.OpenAsync(path);
            var tab = session.OpenStatement(List)!;
            var (view, window) = rig.ShowEditor(tab, session, 1280, 640);
            var vm = view.Expanded!;
            await HeadlessHost.WaitUntil(() => vm.Sql.Length > 0);
            rig.Hub.Refresh();
            var eval = rig.Hub.ForTab(tab)!;
            tab.Binds.Rows.First(r => r.Name.Equals("ENAME", StringComparison.OrdinalIgnoreCase)).Text = "KIM";
            tab.Binds.Rows.First(r => r.Name.Contains("ORDERBY", StringComparison.OrdinalIgnoreCase)).Text = "E.EMPNO";
            eval.Flush();
            await eval.AddScenarioAsync("이름으로 찾기");
            eval.Flush();
            await HeadlessHost.WaitUntil(() => eval.Result is not null && vm.Sql == eval.Result.Sql && vm.ConditionChip.Length > 0);
            Pump();

            var expanded = ManualCapture.Find<MyBatisExpandedView>(view);
            var source = view.GetVisualDescendants().OfType<TextView>().First(t => t.FindAncestorOfType<MyBatisExpandedView>() is null);
            ManualCapture.Save(window, "mybatis-tab",
            [
                new(source, 1, 530, 40),
                new(Named<ComboBox>(expanded, "ScenarioCombo"), 2),
                new(ManualCapture.Find<Button>(expanded, b => AutomationProperties.GetName(b) == "시나리오 추가" || b.Content as string == "시나리오 추가"), 3),
                new(ManualCapture.Find<TextBlock>(expanded, t => t.Text == vm.ConditionChip), 4, -18),
                new(Named<SqlCodeView>(expanded, "SqlView"), 5, 460, 30),
            ]);
            window.Close();
        });
    });

    [Fact]
    public Task Combos() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        await WithMapperAsync(async (rig, path) =>
        {
            var (_, session) = await rig.OpenAsync(path);
            session.OpenStatement(Update);
            var panel = new MyBatisCombosViewModel(rig.Harness.Services) { PostToUi = a => a() };
            await panel.CheckAsync(session, Update);
            panel.SelectedRow = panel.Rows.First(r => r.IsError);
            var view = new MyBatisCombosView { DataContext = panel };
            var window = new Window { Width = 1280, Height = 480, Content = view };
            window.Show();
            Pump();
            var overlay = ManualCapture.AddOverlay(window); // 빠른 고침 띠는 내용을 옮겨 붙이면 새로 만들어진다
            Pump();

            ManualCapture.SaveOverlaid(window, overlay, "mybatis-combos",
            [
                new(ByAutomationName<ComboBox>(view, "조합 방식"), 1, -70, 12),
                new(ManualCapture.Find<TextBlock>(view, t => t.Text == panel.Title), 2, 355, 8),
                new(ByAutomationName<ListBox>(view, "조합 검사 결과"), 3, 420, 130),
                new(Named<SqlCodeView>(view, "DetailSqlView"), 4, 370, 12),
                new(ManualCapture.Find<Border>(view, b => b.Classes.Contains("quick-fix")), 5, 700, 13),
            ]);
            window.Close();
        });
    });

    [Fact]
    public Task Save_diff() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        await WithMapperAsync(async (rig, path) =>
        {
            var (ws, session) = await rig.OpenAsync(path);
            session.OpenStatement(Update);
            var panel = new MyBatisCombosViewModel(rig.Harness.Services) { PostToUi = a => a() };
            await panel.CheckAsync(session, Update);
            await panel.PreviewAsync(panel.Fixes[0]);
            await panel.ApplyFixCommand.ExecuteAsync(null);
            Pump();

            var drawn = false;
            rig.Presenter.Answer = request =>
            {
                var window = new SaveDiffWindow(new SaveDiffViewModel(request));
                window.Show();
                Pump();
                ManualCapture.Save(window, "mybatis-save",
                [
                    new(ManualCapture.Find<MapperDiffView>(window), 1, 300, 12),
                    new(ManualCapture.FindByText(window, SaveDiffViewModel.ChecksHeading), 2, 96),
                    new(ManualCapture.Find<Button>(window, b => b.Classes.Contains("accent")), 3),
                    new(ManualCapture.Find<CheckBox>(window, c => c.Content as string == "저장할 때마다 이 창 보기"), 4),
                ]);
                window.Close();
                drawn = true;
                return null; // 저장하지 않는다
            };

            Assert.False(await rig.Save.SaveAsync(ws, default));
            Assert.True(drawn, "저장 창이 나오지 않았다.");
            Assert.Equal(MapperSamples.EmpMapper, await File.ReadAllTextAsync(path));
        });
    });

    [Fact]
    public Task Convert_dialog() => HeadlessHost.Run(() =>
    {
        if (!ManualCapture.Enabled)
        {
            return;
        }

        IReadOnlyList<ConvertSourceTab> tabs =
        [
            new("주문 조회", "SELECT o.order_id, o.total_amount\nFROM orders o\nWHERE o.status = :status\nAND o.customer_id = :customerId", []),
            new("주문 상태 변경", "UPDATE orders\nSET status = :status\nWHERE order_id = :orderId", []),
            new("고객 등록", "INSERT INTO customers (customer_id, customer_name, region)\nVALUES (:customerId, :customerName, :region)", []),
        ];
        var vm = new ConvertDialogViewModel(
            "주문 업무.sqlw", tabs, Path.Combine(MapperFolder, "OrderMapper.xml"), "com.acme.order.OrderMapper", ConvertWriter.ReadExisting,
            ConvertEngine.Analyze, ConvertEngine.Convert, ConvertEngine.IsValidId);
        var window = new ConvertDialog { DataContext = vm };
        window.Show();
        Pump();

        ManualCapture.Save(window, "mybatis-convert",
        [
            new(Named<RadioButton>(window, "NewFileRadio"), 1),
            new(Named<ListBox>(window, "LineList"), 2, 570, 12),
            new(Named<CheckBox>(window, "JdbcTypeCheck"), 3),
            new(Named<SqlCodeView>(window, "PreviewBox"), 4, 300, 12),
            new(Named<Button>(window, "ConfirmButton"), 5),
        ]);
        window.Close();
    });
}
