using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.ExecutionPlan;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Features.PlsqlWorkspace;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.App.Features.Tuning.Stats;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 실행 계획·튜닝·스키마 장 화면 캡처 생성기. 실제 셸을 로컬 샘플 DB(<c>sample</c>)에 붙여 진짜 결과를 그린다. 조회·계획만 쓰고 데이터는 쓰지 않는다.
/// 번호의 뜻은 <c>site/src/content/docs/plan/execution-plan.md</c>, <c>tuning.md</c>, <c>schema/browser.md</c>, <c>plsql.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualPlanSchemaScreens
{
    private const string IndexSql = "SELECT /*+ INDEX(o orders_date_ix) */ c.region, COUNT(*) AS order_count, SUM(o.total_amount) AS total\nFROM orders o\nJOIN customers c ON c.customer_id = o.customer_id\nWHERE o.order_date >= DATE '2026-01-01'\nGROUP BY c.region\nORDER BY total DESC";

    private const string FullSql = "SELECT /*+ FULL(o) */ c.region, COUNT(*) AS order_count, SUM(o.total_amount) AS total\nFROM orders o\nJOIN customers c ON c.customer_id = o.customer_id\nWHERE o.order_date >= DATE '2026-01-01'\nGROUP BY c.region\nORDER BY total DESC";

    private const string TuningSql = "SELECT o.order_id, o.order_date, o.total_amount\nFROM orders o\nWHERE o.status = 'PAID'\nAND TRUNC(o.order_date) = DATE '2026-03-01'\nORDER BY o.order_date";

    private static readonly string[] ExpandedCategories = ["TABLE", "VIEW", "PACKAGE BODY"];

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>번호 표시 겹침판을 컨트롤을 찾기 전에 한 번만 얹는다(캡처 도우미가 내용을 다시 붙이면 문서 View 가 새로 만들어지기 때문).</summary>
    private static Canvas AddOverlay(Window window)
    {
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        var original = window.Content as Control ?? throw new InvalidOperationException("창 내용이 컨트롤이 아니다.");
        window.Content = null;
        var host = new Grid();
        host.Children.Add(original);
        var overlay = new Canvas { IsHitTestVisible = false };
        host.Children.Add(overlay);
        window.Content = host;
        Pump(window);
        return overlay;
    }

    private static void Badge(Canvas overlay, Control target, int number, double dx, double dy)
    {
        const double diameter = 22;
        var origin = target.TranslatePoint(new Point(0, 0), overlay)
            ?? throw new InvalidOperationException($"번호 {number}: 컨트롤 위치를 알 수 없다.");
        var badge = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D93025")),
            BorderBrush = Avalonia.Media.Brushes.White,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = Avalonia.Media.Brushes.White,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        Canvas.SetLeft(badge, Math.Max(0, origin.X - diameter / 2 + dx));
        Canvas.SetTop(badge, Math.Max(0, origin.Y - diameter / 2 + dy));
        overlay.Children.Add(badge);
    }

    /// <summary>번호 표시 한 개. 글자를 가리지 않게 필요하면 모서리에서 옮긴다.</summary>
    private sealed record Mark(Control Target, int Number, double Dx = 0, double Dy = 0);

    private static void SaveShell(Window window, Canvas overlay, string name, IReadOnlyList<Mark> marks)
    {
        Pump(window);
        foreach (var mark in marks)
        {
            Badge(overlay, mark.Target, mark.Number, mark.Dx, mark.Dy);
        }

        Pump(window);
        ManualCapture.Save(window, name);
    }

    private static T Up<T>(Visual from)
        where T : Visual =>
        from.FindAncestorOfType<T>() ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name} 조상을 찾지 못했다.");

    private static T Named<T>(Visual root, string name)
        where T : Control => ManualCapture.Find<T>(root, c => c.Name == name);

    private static async Task<(ManualShell Shell, Canvas Overlay)> OpenShellAsync()
    {
        var shell = await ManualShell.OpenAsync();
        try
        {
            await shell.ShowOnlySampleSchemaAsync();
            var window = shell.Harness.Window;
            window.Width = 1280;
            window.Height = 800;
            Pump(window);
            return (shell, AddOverlay(window));
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }


    private const string SchemaPanelId = "oranda.panel.schemaBrowser";

    private static async Task<SchemaNode> SelectNodeAsync(ManualShell shell, string category, string name)
    {
        var browser = shell.Panel<SchemaBrowserViewModel>(SchemaPanelId)!;
        var sample = browser.Roots.Single(r => r.Kind == SchemaNodeKind.Schema);
        await browser.ExpandAsync(sample);
        sample.IsExpanded = true;
        var folder = sample.Children.First(c => c.CategoryType == category);
        await browser.ExpandAsync(folder);
        folder.IsExpanded = true;
        var node = folder.Children.First(c => c.Title == name);
        browser.SelectedNode = node;
        Pump(shell.Harness.Window);
        return node;
    }

    /// <summary>스키마 브라우저를 닫아 문서·계획 창에 가로 폭을 준다(번호 표시는 그 뒤에 얹는다).</summary>
    private static void HideSchemaBrowser(ManualShell shell)
    {
        var docking = shell.Harness.Get<DockWorkspace>();
        docking.HideTool(SchemaPanelId);
        docking.HideTool("oranda.panel.scripts");
        Pump(shell.Harness.Window);
    }

    /// <summary>도구 창이 들어 있는 세로 분할에서 그 쪽 몫을 키운다.</summary>
    private static void GiveRoomTo(ManualShell shell, string toolId, double share)
    {
        var stack = new Stack<global::Dock.Model.Core.IDockable>();
        stack.Push(shell.Harness.Get<DockWorkspace>().Layout!);
        PanelTool? tool = null;
        while (stack.Count > 0 && tool is null)
        {
            var d = stack.Pop();
            if (d is PanelTool t && t.Definition.Id == toolId)
            {
                tool = t;
            }

            foreach (var child in (d as global::Dock.Model.Core.IDock)?.VisibleDockables ?? [])
            {
                stack.Push(child);
            }
        }

        global::Dock.Model.Core.IDockable cur = tool ?? throw new Xunit.Sdk.XunitException("도구 창을 찾지 못했다: " + toolId);
        while (cur.Owner is { } owner)
        {
            if (owner is global::Dock.Model.Controls.IProportionalDock { Orientation: global::Dock.Model.Core.Orientation.Vertical } split)
            {
                var others = (split.VisibleDockables ?? []).Where(x => !ReferenceEquals(x, cur) && x is not global::Dock.Model.Controls.IProportionalDockSplitter).ToList();
                cur.Proportion = share;
                foreach (var other in others)
                {
                    other.Proportion = (1 - share) / others.Count;
                }

                Pump(shell.Harness.Window);
                return;
            }

            cur = owner;
        }
    }

    private static async Task ExecAsync(ManualShell shell, string id, object? parameter = null)
    {
        var registry = shell.Harness.Get<ICommandRegistry>();
        var keys = shell.Harness.Get<IContextKeys>();
        var run = registry.ExecuteAsync(id, keys, parameter);
        await HeadlessHost.WaitUntil(() => run.IsCompleted, 90000);
        Assert.True(await run, $"{id} 를 실행하지 못했다(비활성).");
        Dispatcher.UIThread.RunJobs();
    }

    private static PlanViewModel ActivePlan(ManualShell shell) =>
        shell.Panel<PlanPanelViewModel>(ExecutionPlanCommands.PanelId)?.Active ?? throw new Xunit.Sdk.XunitException("계획 창에 활성 계획이 없다.");

    /// <summary>예상 계획을 만들고 트리에 줄이 채워질 때까지 기다린다.</summary>
    private static async Task ExplainAsync(ManualShell shell, SqlEditorViewModel document)
    {
        ManualShell.Host.Active = document;
        Dispatcher.UIThread.RunJobs();
        await ExecAsync(shell, "oranda.plan.explain");
        await HeadlessHost.WaitUntil(() => shell.Panel<PlanPanelViewModel>(ExecutionPlanCommands.PanelId)?.Active is { Rows.Count: > 0 }, 30000);
        Pump(shell.Harness.Window);
    }

    [Fact]
    public Task Plan_tree() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            var document = ManualShell.Host.NewDocument(IndexSql);
            await ExplainAsync(shell, document);
            HideSchemaBrowser(shell);
            GiveRoomTo(shell, ExecutionPlanCommands.PanelId, 0.78);
            var view = ManualCapture.Find<PlanView>(window);
            SaveShell(window, overlay, "plan-tree",
            [
                new Mark(Up<StackPanel>(Named<Button>(view, "ExplainButton")), 1),
                new Mark(Named<ListBox>(view, "ViewSwitch"), 2),
                new Mark(Named<Button>(view, "FormatButton"), 3),
                new Mark(Named<Button>(view, "PinButton"), 4),
                new Mark(Named<Button>(view, "StatsButton"), 5),
                new Mark(Named<Control>(view, "PlanList"), 6, 0, 20),
                new Mark(Named<Control>(view, "DetailsPane"), 7),
            ]);
        }
    });

    [Fact]
    public Task Plan_compare() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            var document = ManualShell.Host.NewDocument(FullSql);
            await ExplainAsync(shell, document);
            await ExecAsync(shell, "oranda.plan.pinForCompare");
            document.Document.Text = IndexSql;
            await ExplainAsync(shell, document);
            await ExecAsync(shell, "oranda.plan.compare");
            HideSchemaBrowser(shell);
            GiveRoomTo(shell, ExecutionPlanCommands.PanelId, 0.78);
            var plan = ActivePlan(shell);
            await HeadlessHost.WaitUntil(() => plan.CompareRows.Count > 0, 10000);
            var view = ManualCapture.Find<PlanView>(window);
            var chip = Up<Border>(ManualCapture.Find<TextBlock>(view, t => t.Text == plan.PinnedText && plan.PinnedText.Length > 0));
            var summary = ManualCapture.Find<TextBlock>(view, t => t.Text == plan.CompareSummaryText && plan.CompareSummaryText.Length > 0);
            var header = Up<Grid>(ManualCapture.FindByText(view, "기준 Id"));
            var rows = ManualCapture.Find<ListBox>(view, l => ReferenceEquals(l.ItemsSource, plan.CompareRows));
            SaveShell(window, overlay, "plan-compare",
            [
                new Mark(Named<ListBox>(view, "ViewSwitch"), 1),
                new Mark(chip, 2),
                new Mark(summary, 3, 760),
                new Mark(header, 4, 440),
                new Mark(rows, 5, 440, 15),
            ]);
        }
    });

    [Fact]
    public Task Tuning_assistant() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            await shell.RunAsync(TuningSql);
            await ExecAsync(shell, "oranda.tuning.assistant");
            var view = ManualCapture.Find<TuningAssistantView>(window);
            var vm = (TuningAssistantViewModel)view.DataContext!;
            await HeadlessHost.WaitUntil(() => !vm.IsAnalyzing, 60000);
            if (vm.IndexCount == 0 && vm.CanFetchEstimatedPlan)
            {
                await vm.FetchEstimatedPlanAsync(); // 실제 계획을 못 읽는 계정이면 화면의 「예상 계획으로 분석」 단추와 같은 길로 계획을 받는다
                await HeadlessHost.WaitUntil(() => !vm.IsAnalyzing, 60000);
            }

            HideSchemaBrowser(shell);
            ManualShell.Trace($"assistant status='{vm.StatusText}' idx={vm.IndexCount} rw={vm.RewriteCount} hint={vm.HintCount} canRun={vm.CanRunBenchmark}");
            vm.CandidateTab = vm.IndexCount > 0 ? 0 : 1;
            Pump(window);
            if (vm.CanRunBenchmark)
            {
                await vm.RunBenchmarkAsync();
                Pump(window);
            }

            view = ManualCapture.Find<TuningAssistantView>(window);
            SaveShell(window, overlay, "tuning-assistant",
            [
                new Mark(ManualCapture.Find<Border>(view, b => b.Classes.Contains("chip")), 1),
                new Mark(Named<Button>(view, "RunButton"), 2),
                new Mark(Named<Button>(view, "ReportButton"), 3),
                new Mark(Named<TabControl>(view, "CandidateTabs"), 4),
                new Mark(Named<ListBox>(view, vm.CandidateTab == 0 ? "IndexList" : "RewriteList"), 5),
                new Mark(ManualCapture.Find<Border>(view, b => b.Classes.Contains("detail-pane")), 6),
                new Mark(Named<ListBox>(view, "BenchmarkList"), 7),
            ]);
        }
    });

    [Fact]
    public Task Tuning_stats() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            await ExecAsync(shell, "oranda.tuning.stats", "SAMPLE.ORDERS");
            var vm = (TableStatsViewModel)ManualCapture.Find<TableStatsView>(window).DataContext!;
            await HeadlessHost.WaitUntil(() => vm.ShowContent, 60000);
            foreach (var column in vm.Columns)
            {
                vm.SelectedColumn = column;
                try
                {
                    await HeadlessHost.WaitUntil(() => vm.HasHistogram, 2500);
                    break;
                }
                catch (TimeoutException)
                {
                    // 이 열은 히스토그램이 없다 — 다음 열을 본다
                }
            }

            ManualShell.Trace($"stats hist={vm.HasHistogram} sel={vm.SelectedColumn?.Name}");
            HideSchemaBrowser(shell);
            var view = ManualCapture.Find<TableStatsView>(window);
            SaveShell(window, overlay, "tuning-stats",
            [
                new Mark(Named<TextBox>(view, "TargetBox"), 1),
                new Mark(Named<Button>(view, "RefreshButton"), 2, 20),
                new Mark(Named<Button>(view, "ScriptButton"), 3),
                new Mark(Named<WrapPanel>(view, "SummaryLine"), 4, 0, -14),
                new Mark(Named<Control>(view, "ColumnTable"), 5),
                new Mark(Named<ItemsControl>(view, "HistogramList"), 6),
                new Mark(Named<Control>(view, "IndexTable"), 7),
            ]);
        }
    });

    [Fact]
    public Task Schema_browser_tree() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            var browser = shell.Panel<SchemaBrowserViewModel>("oranda.panel.schemaBrowser")!;
            var sample = browser.Roots.Single(r => r.Kind == SchemaNodeKind.Schema);
            await browser.ExpandAsync(sample);
            sample.IsExpanded = true;
            foreach (var type in ExpandedCategories)
            {
                if (sample.Children.FirstOrDefault(c => c.CategoryType == type) is { } category)
                {
                    await browser.ExpandAsync(category);
                    category.IsExpanded = true;
                }
            }

            Pump(window);
            var view = ManualCapture.Find<SchemaBrowserView>(window);
            SaveShell(window, overlay, "schema-browser-tree",
            [
                new Mark(Named<Control>(view, "TypeBox"), 1),
                new Mark(Named<Button>(view, "SavedFilterButton"), 2),
                new Mark(Named<ToggleButton>(view, "InvalidToggle"), 3),
                new Mark(Named<ToggleButton>(view, "FavoritesToggle"), 4),
                new Mark(Named<Button>(view, "RefreshButton"), 5),
                new Mark(Named<TextBox>(view, "FilterBox"), 6),
                new Mark(Named<TreeView>(view, "Tree"), 7, 215, 90),
            ]);
        }
    });

    [Fact]
    public Task Object_detail() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            await SelectNodeAsync(shell, "TABLE", "ORDERS");
            await ExecAsync(shell, "oranda.schema.openDetail", new DbObjectRef("SAMPLE", "ORDERS", "TABLE"));
            HideSchemaBrowser(shell);
            var view = ManualCapture.Find<ObjectDetailView>(window);
            var vm = (ObjectDetailViewModel)view.DataContext!;
            await HeadlessHost.WaitUntil(() => vm.Sections.Count > 0 && vm.SelectedSection is not null, 60000);
            Pump(window);
            var status = ManualCapture.Find<TextBlock>(view, t => t.Text == vm.StatusText && vm.StatusText.Length > 0);
            SaveShell(window, overlay, "object-detail",
            [
                new Mark(Named<TabControl>(view, "Tabs"), 1),
                new Mark(Named<ListBox>(view, "SectionList"), 2),
                new Mark(Named<Button>(view, "ReorderColumnsButton"), 3, 120),
                new Mark(Named<Control>(view, "SectionTable"), 4),
                new Mark(status, 5, 420),
            ]);
        }
    });

    [Fact]
    public Task Object_search() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            await ExecAsync(shell, "oranda.schema.objectSearch");
            HideSchemaBrowser(shell);
            var view = ManualCapture.Find<ObjectSearchView>(window);
            var vm = (ObjectSearchViewModel)view.DataContext!;
            vm.NameText = "ORDER";
            await vm.SearchAsync();
            await HeadlessHost.WaitUntil(() => vm.Results.Count > 0, 30000);
            vm.SelectedResult = vm.Results.FirstOrDefault(r => r.Ref.Name == "ORDERS") ?? vm.Results[0];
            await HeadlessHost.WaitUntil(() => vm.ReferencesText.Length > 0, 15000);
            Pump(window);
            var status = ManualCapture.Find<TextBlock>(view, t => t.Text == vm.StatusText && vm.StatusText.Length > 0);
            SaveShell(window, overlay, "object-search",
            [
                new Mark(Named<TabControl>(view, "Tabs"), 1),
                new Mark(Named<TextBox>(view, "NameBox"), 2),
                new Mark(Named<Control>(view, "TypeBox"), 3),
                new Mark(Named<Button>(view, "SearchButton"), 4),
                new Mark(Named<ListBox>(view, "ResultList"), 5, 0, 10),
                new Mark(ManualCapture.Find<SelectableTextBlock>(view), 6, 100, 4),
                new Mark(status, 7, 420),
            ]);
        }
    });

    [Fact]
    public Task Plsql_editor() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, overlay) = await OpenShellAsync();
        using (shell)
        {
            var window = shell.Harness.Window;
            await SelectNodeAsync(shell, "PACKAGE BODY", "SAMPLE_PKG");
            await ExecAsync(shell, "oranda.plsql.openSource", new DbObjectRef("SAMPLE", "SAMPLE_PKG", "PACKAGE BODY"));
            HideSchemaBrowser(shell);
            var view = ManualCapture.Find<PlsqlWorkspaceView>(window);
            var workspace = (PlsqlWorkspaceViewModel)view.DataContext!;
            await HeadlessHost.WaitUntil(() => workspace.ActiveTab is { Editor.Document.TextLength: > 0 }, 30000);
            Pump(window);
            var editor = ManualCapture.Find<PlsqlEditorView>(view);
            var tabs = Named<ListBox>(view, "PlsqlTabs");
            var status = ManualCapture.Find<TextBlock>(editor, t => t.Text == workspace.ActiveTab!.StatusText && workspace.ActiveTab.StatusText.Length > 0);
            SaveShell(window, overlay, "plsql-editor",
            [
                new Mark(tabs, 1),
                new Mark(ManualCapture.Find<Button>(view, b => AutomationProperties.GetName(b) == "새 PL/SQL 탭" || b.Content as string == "새 PL/SQL 탭"), 2),
                new Mark(Named<Button>(editor, "CompileButton"), 3),
                new Mark(Named<Button>(editor, "DiffButton"), 4),
                new Mark(Named<Button>(editor, "SiblingButton"), 5),
                new Mark(Named<SqlEditorView>(editor, "EditorView"), 6, 0, 10),
                new Mark(status, 7, 420),
            ]);
        }
    });

    [Fact]
    public Task Plsql_run() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        var (shell, _) = await OpenShellAsync();
        using (shell)
        {
            var main = shell.Harness.Window;
            await SelectNodeAsync(shell, "PACKAGE BODY", "SAMPLE_PKG");
            await ExecAsync(shell, "oranda.plsql.runFromBrowser", new DbObjectRef("SAMPLE", "SAMPLE_PKG", "PACKAGE BODY"));
            var window = main.OwnedWindows.OfType<PlsqlRunWindow>().FirstOrDefault()
                ?? throw new Xunit.Sdk.XunitException("PL/SQL 실행 창을 찾지 못했다.");
            var vm = (PlsqlRunViewModel)window.DataContext!;
            await HeadlessHost.WaitUntil(() => vm.Rows.Count > 0, 30000);
            var pick = vm.Candidates.FirstOrDefault(c => c.Display.Contains("CUSTOMER_TOTAL", StringComparison.OrdinalIgnoreCase));
            if (pick is not null)
            {
                vm.SelectedCandidate = pick;
            }

            Pump(window);
            vm.Rows.First(r => r.CanEdit).Text = "1";
            Assert.True(await vm.RunAsync(), vm.StatusText);
            Pump(window);
            var overlay = AddOverlay(window);
            var picker = ManualCapture.Find<ComboBox>(window);
            var header = Up<Border>(ManualCapture.FindByText(window, "인자"));
            var value = ManualCapture.Find<TextBox>(window, t => (AutomationProperties.GetName(t) ?? string.Empty).EndsWith(" 값", StringComparison.Ordinal) && t.IsVisible);
            var isNull = ManualCapture.Find<CheckBox>(window, c => (AutomationProperties.GetName(c) ?? string.Empty).EndsWith(" NULL", StringComparison.Ordinal) && c.IsVisible);
            var outBox = ManualCapture.Find<TextBox>(window, t => (AutomationProperties.GetName(t) ?? string.Empty).EndsWith(" 결과", StringComparison.Ordinal) && t.IsVisible && t.Text?.Length > 0);
            var tabs = ManualCapture.Find<TabControl>(window);
            var run = ManualCapture.FindByText(window, "실행");
            SaveShell(window, overlay, "plsql-run",
            [
                new Mark(picker, 1),
                new Mark(header, 2, 100),
                new Mark(value, 3),
                new Mark(isNull, 4),
                new Mark(outBox, 5),
                new Mark(tabs, 6),
                new Mark(Up<StackPanel>(run), 7),
            ]);
        }
    });
}
