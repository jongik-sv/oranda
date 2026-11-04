using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;
using Dock.Model.Core;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 「실행」 장 화면 캡처 생성기. 실제 셸을 샘플 DB(<c>sample</c>)에 접속해 지어낸 조회를 실행한 결과만 그린다.
/// 쓰기(DML)는 하지 않는다: 그리드 편집은 메모리에 고친 값만 쌓고 DML 미리보기를 연 뒤 닫는다(반영·커밋 없음).
/// 번호의 뜻은 <c>site/src/content/docs/run/execute.md</c>, <c>result-grid.md</c>, <c>grid-edit.md</c> 의 번호별 설명과 같아야 한다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ManualRunScreens
{
    private const string EmployeesSql = "SELECT e.emp_id, e.emp_name, d.dept_name, e.job_title, e.salary, e.hire_date\nFROM employees e\nJOIN departments d ON d.dept_id = e.dept_id\nWHERE e.dept_id IN (20, 40)\nORDER BY e.emp_id";

    private const string ProductsSql = "SELECT product_id, product_name, category, unit_price\nFROM products\nORDER BY product_id";

    /// <summary>번호 표시 한 개. <paramref name="Ax"/>·<paramref name="Ay"/> 는 컨트롤 안의 위치(0~1), <paramref name="Dx"/>·<paramref name="Dy"/> 는 그 점에서 옮길 픽셀.</summary>
    private sealed record Mark(Visual Target, int Number, double Ax = 0, double Ay = 0, double Dx = 0, double Dy = 0);

    private static Window? s_owner;

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Button ButtonNamed(Visual root, string name) =>
        ManualCapture.Find<Button>(root, b => AutomationProperties.GetName(b) == name || b.Content as string == name);

    private static Button ButtonTagged(Visual root, string tag) =>
        ManualCapture.Find<Button>(root, b => b.Tag as string == tag);

    private static T Up<T>(Visual from)
        where T : Visual =>
        from.FindAncestorOfType<T>() ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name} 조상을 찾지 못했다.");

    /// <summary>셸 창은 내용을 옮겨 붙이면 View 가 새로 만들어지므로, 번호 겹침판을 컨트롤을 찾기 전에 한 번만 얹는다.</summary>
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

    private static void Badge(Canvas overlay, Mark mark)
    {
        var target = mark.Target as Control ?? throw new InvalidOperationException($"번호 {mark.Number}: 컨트롤이 아니다.");
        var size = target.Bounds.Size;
        var origin = target.TranslatePoint(new Point(size.Width * mark.Ax, size.Height * mark.Ay), overlay)
            ?? throw new InvalidOperationException($"번호 {mark.Number}: 컨트롤 위치를 알 수 없다.");
        PlaceBadge(overlay, mark.Number, origin.X + mark.Dx, origin.Y + mark.Dy);
    }

    private static void PlaceBadge(Canvas overlay, int number, double x, double y)
    {
        const double diameter = 22;
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
        Canvas.SetLeft(badge, Math.Max(0, x - diameter / 2));
        Canvas.SetTop(badge, Math.Max(0, y - diameter / 2));
        overlay.Children.Add(badge);
    }

    private static void SaveWith(Window window, Canvas overlay, string name, IReadOnlyList<Mark> marks)
    {
        foreach (var mark in marks)
        {
            Badge(overlay, mark);
        }

        Pump(window);
        ManualCapture.Save(window, name);
    }

    private static PanelTool ToolOf(ManualShell shell, string panelId)
    {
        var stack = new Stack<IDockable>();
        stack.Push(shell.Harness.Get<DockWorkspace>().Layout!);
        while (stack.Count > 0)
        {
            var dockable = stack.Pop();
            if (dockable is PanelTool tool && tool.Definition.Id == panelId)
            {
                return tool;
            }

            foreach (var child in (dockable as IDock)?.VisibleDockables ?? [])
            {
                stack.Push(child);
            }
        }

        throw new Xunit.Sdk.XunitException("도구 창을 찾지 못했다: " + panelId);
    }

    /// <summary>아래쪽 도구 창 묶음(조회 결과·DBMS 출력)이 행을 여러 줄 보이게 편집기보다 크게 만든다.</summary>
    private static void GiveBottomRoom(ManualShell shell, Window window)
    {
        var bottom = ToolOf(shell, "oranda.panel.results").Owner as IDock ?? throw new Xunit.Sdk.XunitException("아래 묶음을 찾지 못했다.");
        if (bottom.Owner is not IDock parent)
        {
            return;
        }

        foreach (var sibling in (parent.VisibleDockables ?? []).Where(d => d is not IProportionalDockSplitter))
        {
            sibling.Proportion = ReferenceEquals(sibling, bottom) ? 0.72 : 0.28;
        }

        Pump(window);
    }

    /// <summary>도구 창 묶음 하나가 같은 줄의 다른 묶음보다 <paramref name="share"/> 만큼 넓게(높게) 보이게 한다.</summary>
    private static void GiveRoom(ManualShell shell, Window window, string panelId, double share)
    {
        if (ToolOf(shell, panelId).Owner is not IDock dock || dock.Owner is not IDock parent)
        {
            return;
        }

        var others = (parent.VisibleDockables ?? []).Where(d => d is not IProportionalDockSplitter && !ReferenceEquals(d, dock)).ToList();
        var sum = others.Sum(d => double.IsNaN(d.Proportion) ? 1.0 / (others.Count + 1) : d.Proportion);
        foreach (var other in others)
        {
            var old = double.IsNaN(other.Proportion) ? 1.0 / (others.Count + 1) : other.Proportion;
            other.Proportion = old / sum * (1 - share);
        }

        dock.Proportion = share;
        Pump(window);
    }

    /// <summary>접속한 셸을 1280x800 으로 띄우고 스키마 브라우저를 <c>SAMPLE</c> 하나로 거른다.</summary>
    private static async Task<ManualShell> OpenShellAsync()
    {
        ExecutionDialogs.Current = new AvaloniaExecutionDialogs(() => s_owner);
        // 앞 시험의 셸에 묶인 정적 연결(??= 로 한 번만 걸린다)이 남지 않게 비워 새 셸이 다시 건다.
        GridEditCommands.Rerun = null;
        GridEditCommands.ConfirmPreview = null;
        GridEditCommands.Report = null;
        GridEditCommands.Active = null;
        var shell = await ManualShell.OpenAsync();
        try
        {
            await shell.ShowOnlySampleSchemaAsync();
            var window = shell.Harness.Window;
            window.Width = 1280;
            window.Height = 800;
            Pump(window);
            // 헤드리스에는 주 창(MainWindow)이 없어 대화상자가 독립 창으로 뜬다. 셸 창을 소유자로 알려 모달로 열리게 한다(처음 부른 값이 남으므로 창은 필드로 바꾼다).
            s_owner = window;
            return shell;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    /// <summary>새 SQL 탭에 <paramref name="sql"/> 을 넣고 명령을 시작만 한다(대화상자가 열리면 끝나지 않는다).</summary>
    private static async Task<Task<bool>> StartRunAsync(ManualShell shell, string sql, string command)
    {
        var document = ManualShell.Host.NewDocument(sql);
        ManualShell.Host.Active = document;
        Dispatcher.UIThread.RunJobs();
        // 새 탭이 접속을 받기 전에 실행하면 「접속 안 됨」 확인 창이 뜬다(첫 셸에서 간헐).
        await HeadlessHost.WaitUntil(() => document.ConnectionProfileId.HasValue, 15000);
        return shell.Harness.Get<ICommandRegistry>().ExecuteAsync(command, shell.Harness.Get<IContextKeys>());
    }

    private static string Describe(Window owner) => string.Join(" || ", owner.OwnedWindows.Select(w =>
        $"{w.Title}: " + string.Join(" | ", w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)))));

    private static async Task FinishAsync(Task<bool> run, Window? owner = null)
    {
        try
        {
            await HeadlessHost.WaitUntil(() => run.IsCompleted, 20000);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException("실행이 끝나지 않았다. 열린 창: " + (owner is null ? "(?)" : Describe(owner)));
        }

        Assert.True(await run);
        Dispatcher.UIThread.RunJobs();
    }

    private static Window WaitDialog(Window owner) => owner.OwnedWindows.Count > 0 ? owner.OwnedWindows[0] : throw new Xunit.Sdk.XunitException("대화상자가 열리지 않았다.");

    private static async Task<Window> OpenedDialogAsync(Window owner, Task<bool>? run = null)
    {
        try
        {
            await HeadlessHost.WaitUntil(() => owner.OwnedWindows.Count > 0, 20000);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"대화상자가 열리지 않았다. 실행 상태 {run?.Status}");
        }

        var dialog = WaitDialog(owner);
        await HeadlessHost.WaitUntil(() => dialog.Bounds.Width > 0 && dialog.IsVisible, 10000);
        Pump(dialog);
        return dialog;
    }

    /// <summary>대화상자의 번호 표시가 글자를 가리지 않게 왼쪽 여백을 넓힌다(캡처에만 쓴다).</summary>
    private static void WidenLeftMargin(Window dialog)
    {
        if (dialog.Content is Control { Margin: var m } root)
        {
            root.Margin = new Thickness(34, m.Top, m.Right, m.Bottom);
            Pump(dialog);
        }
    }

    // ---- 바인드 변수 창 ---------------------------------------------------------------------------------------------

    [Fact]
    public Task Bind_variables() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        const string sql = "SELECT emp_name, job_title, salary\nFROM employees\nWHERE dept_id = :dept_id\nAND salary >= :min_salary\nORDER BY salary DESC";
        // 처음 보는 이름이 있는 문장은 실행하지 않고 바인드 변수 창을 열어 줄을 만든다. 값을 채운 뒤 다시 실행한다.
        var first = await StartRunAsync(shell, sql, "oranda.exec.runStatement");
        await FinishAsync(first);
        var vm = shell.Panel<BindVariablesViewModel>("oranda.panel.binds") ?? throw new Xunit.Sdk.XunitException("바인드 변수 창을 찾지 못했다.");
        Assert.Equal(2, vm.Rows.Count);
        vm.Rows[0].Type = Oranda.Core.Execution.BindDataType.Number;
        vm.Rows[0].Text = "40";
        vm.Rows[1].Type = Oranda.Core.Execution.BindDataType.Number;
        vm.Rows[1].Text = "4000000";
        Pump(window);
        var registry = shell.Harness.Get<ICommandRegistry>();
        var second = registry.ExecuteAsync("oranda.exec.runStatement", shell.Harness.Get<IContextKeys>());
        await FinishAsync(second);
        vm.SelectedRows = [];
        vm.Selected = vm.Rows[0];
        Pump(window);

        GiveRoom(shell, window, "oranda.panel.binds", 0.36);
        var overlay = AddOverlay(window);
        var view = ManualCapture.Find<BindVariablesView>(window);
        var head = ManualCapture.Find<Grid>(view, g => g.Name == "HeaderGrid");
        TextBlock Head(string text) => ManualCapture.Find<TextBlock>(head, t => t.Text == text);
        SaveWith(window, overlay, "bind-variables",
        [
            new Mark(ButtonNamed(view, "바인드 변수 더하기"), 1, 0, 0),
            new Mark(ButtonNamed(view, "고른 변수 지우기"), 2, 0, 0),
            new Mark(ButtonNamed(view, "목록 비우기"), 3, 0, 0),
            new Mark(ButtonNamed(view, "편집기에 값 붙여 넣기"), 4, 0, 0),
            new Mark(Up<Border>(Head("변수")), 5, 1, 0.5, -14),
            new Mark(Up<Border>(Head("값")), 6, 1, 0.5, -14),
            new Mark(Up<Border>(Head("자료형")), 7, 1, 0.5, -14),
            new Mark(Up<Border>(Head("방향")), 7, 0.5, 0.5),
            new Mark(Up<Border>(ManualCapture.FindByText(view, vm.StatusText)), 8, 1, 0.5, -14),
        ]);
    });

    // ---- 스크립트 오류 창 ---------------------------------------------------------------------------------------------

    [Fact]
    public Task Script_error_dialog() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        // 조회만 쓴다. 가운데 문장은 없는 표를 읽어 ORA-00942 로 실패한다.
        const string script = "SELECT COUNT(*) AS emp_count FROM employees;\nSELECT * FROM employee_bonus WHERE dept_id = 40;\nSELECT dept_name FROM departments ORDER BY dept_id;";
        var run = await StartRunAsync(shell, script, "oranda.exec.runScript");
        var dialog = await OpenedDialogAsync(window, run);
        Assert.Equal("스크립트 오류", dialog.Title);
        WidenLeftMargin(dialog);
        var overlay = AddOverlay(dialog);
        var error = ManualCapture.Find<TextBlock>(dialog, t => t.Text?.Contains("ORA-00942", StringComparison.Ordinal) == true);
        SaveWith(dialog, overlay, "script-error-dialog",
        [
            new Mark(error, 1, 0, 0.5, -14),
            new Mark(ManualCapture.Find<Oranda.App.Features.SqlEditor.SqlCodeView>(dialog), 2, 0, 0, -14, 10),
            new Mark(ManualCapture.Find<Button>(dialog, b => b.Content as string == "중지"), 3, 0.5, 0, 0, -6),
            new Mark(ManualCapture.Find<Button>(dialog, b => b.Content as string == "계속"), 4, 0.5, 0, 0, -6),
            new Mark(ManualCapture.Find<Button>(dialog, b => b.Content as string == "모두 계속"), 5, 0.5, 0, 0, -6),
        ]);
        dialog.Close(); // 창 닫기 = 「중지」
        await FinishAsync(run);
    });

    // ---- DBMS 출력 창 ---------------------------------------------------------------------------------------------

    [Fact]
    public Task Output_pane() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        // 조회와 출력만 하는 익명 블록(데이터를 바꾸지 않는다).
        const string block = "DECLARE\n  v_count NUMBER;\nBEGIN\n  DBMS_OUTPUT.PUT_LINE('부서별 사원 수');\n  FOR r IN (SELECT d.dept_name, COUNT(*) AS cnt FROM departments d JOIN employees e ON e.dept_id = d.dept_id GROUP BY d.dept_name ORDER BY d.dept_name) LOOP\n    DBMS_OUTPUT.PUT_LINE(r.dept_name || ': ' || r.cnt);\n  END LOOP;\n  SELECT COUNT(*) INTO v_count FROM employees WHERE salary >= 5000000;\n  DBMS_OUTPUT.PUT_LINE('급여 500만 이상: ' || v_count);\nEND;";
        var run = await StartRunAsync(shell, block, "oranda.exec.runStatement");
        await FinishAsync(run);
        Assert.True(shell.Harness.Get<DockWorkspace>().ActivateTool("oranda.panel.output"));
        GiveBottomRoom(shell, window);
        Pump(window);
        var overlay = AddOverlay(window);
        var view = ManualCapture.Find<OutputView>(window);
        SaveWith(window, overlay, "output-pane",
        [
            new Mark(ButtonNamed(view, "출력 지우기"), 1, 0, 0),
            new Mark(ManualCapture.FindByText(view, "DBMS_OUTPUT"), 2, 0, 0.5, 100),
            new Mark(ManualCapture.Find<TextBox>(view, t => t.Name == "OutputBox"), 3, 1, 0, -30, 20),
        ]);
    });

    // ---- 결과 그리드 ---------------------------------------------------------------------------------------------

    private static async Task<(SqlResultPaneView View, SqlResultPaneViewModel Pane, ResultGridControl Grid)> RunEmployeesAsync(ManualShell shell, Window window)
    {
        var run = await StartRunAsync(shell, EmployeesSql, "oranda.exec.runStatement");
        await FinishAsync(run);
        GiveBottomRoom(shell, window);
        var view = ManualCapture.Find<SqlResultPaneView>(window);
        var grid = ManualCapture.Find<ResultGridControl>(view);
        await HeadlessHost.WaitUntil(() => grid.Model is { HasResult: true, RowCount: > 10 }, 20000);
        return (view, (SqlResultPaneViewModel)view.DataContext!, grid);
    }

    [Fact]
    public Task Result_grid_pane() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        var (view, pane, grid) = await RunEmployeesAsync(shell, window);
        var model = grid.Model!;
        await model.ToggleSortAsync(4); // salary 오름차순
        await model.ToggleSortAsync(4); // 내림차순 ▼
        pane.IsQuickFilterVisible = true;
        pane.IsFindVisible = true;
        pane.IsFilterBarVisible = true;
        model.SelectCell(new GridCell(1, 4));
        model.ExtendTo(new GridCell(4, 4)); // 급여 칸 네 개 → 선택 집계
        Pump(window);
        await HeadlessHost.WaitUntil(() => pane.HasSelectionSummary, 5000);
        Pump(window);
        var overlay = AddOverlay(window);
        view = ManualCapture.Find<SqlResultPaneView>(window);
        grid = ManualCapture.Find<ResultGridControl>(view);
        model = grid.Model!;

        // 열 머리글·행 머리·셀 위치는 모델의 열 폭과 그리드의 줄 높이에서 계산한다.
        var lefts = new List<double>();
        var x = grid.RowHeaderWidth;
        foreach (var ordinal in model.VisibleColumns)
        {
            lefts.Add(x);
            x += model.GetColumnWidth(ordinal);
        }

        double Width(int col) => model.GetColumnWidth(model.VisibleColumns[col]);
        Point InGrid(double gx, double gy) => grid.TranslatePoint(new Point(gx, gy), overlay) ?? throw new InvalidOperationException("그리드 위치를 알 수 없다.");
        var headerPoint = InGrid(lefts[5] + Width(5) - 14, grid.HeaderHeight / 2); // 입사일 열 머리글(빈 오른쪽)
        var rowHeadPoint = InGrid(grid.RowHeaderWidth / 2, grid.HeaderHeight + (grid.RowHeight * 8.5)); // 현재 행(▶)이 아닌 행의 행 머리 칸
        var cellPoint = InGrid(lefts[3] + Width(3) - 14, grid.HeaderHeight + (grid.RowHeight * 10.5)); // 직급 칸의 빈 오른쪽
        PlaceBadge(overlay, 4, headerPoint.X, headerPoint.Y);
        PlaceBadge(overlay, 5, rowHeadPoint.X, rowHeadPoint.Y);
        PlaceBadge(overlay, 6, cellPoint.X, cellPoint.Y);
        SaveWith(window, overlay, "result-grid-pane",
        [
            new Mark(ManualCapture.Find<TextBox>(view, t => t.Name == "QuickFilterBox"), 1, 1, 0.5, -16),
            new Mark(ManualCapture.Find<TextBox>(view, t => t.Name == "FindBox"), 2, 1, 0.5, -16),
            new Mark(ManualCapture.Find<TextBox>(view, t => t.Name == "WhereBox"), 3, 1, 0.5, -16),
            new Mark(ManualCapture.Find<TextBlock>(view, t => t.Text == pane.StatusText && pane.StatusText.Length > 0), 7, 1, 0.5, 14),
            new Mark(ManualCapture.Find<TextBlock>(view, t => t.Name == "SelectionSummaryText"), 8, 0, 0.5, -14),
        ]);
    });

    [Fact]
    public Task Result_column_filter() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        var (view, pane, grid) = await RunEmployeesAsync(shell, window);
        _ = pane;
        grid.Model!.SelectCell(new GridCell(1, 3));
        var overlay = AddOverlay(window);
        view = ManualCapture.Find<SqlResultPaneView>(window);
        grid = ManualCapture.Find<ResultGridControl>(view);
        var host = ManualCapture.Find<Popup>(view, p => p.Name == "ColumnFilterHost");
        grid.RequestColumnFilter(3); // 직급 열 머리글의 ▾ 와 같다
        await HeadlessHost.WaitUntil(() => host.IsOpen && host.Child is not null, 20000);
        Pump(window);
        var panel = ManualCapture.Find<ColumnFilterPanel>(host.Child!);
        // 조건 「포함」 과 값을 채워 두면 값 칸이 보인다(적용하지 않는다).
        var kind = ManualCapture.Find<ComboBox>(panel);
        kind.SelectedIndex = 1;
        Pump(window);
        var value = ManualCapture.Find<TextBox>(panel, t => t.PlaceholderText == "값");
        value.Text = "장";
        Pump(window);

        // 팝업은 겹침층에 따로 뜨므로 번호 표시는 팝업 안쪽(내용 위)에 더한다.
        var inner = panel.Child ?? throw new Xunit.Sdk.XunitException("열 필터 내용이 비었다.");
        panel.Child = null;
        var holder = new Grid();
        holder.Children.Add(inner);
        var badges = new Canvas { IsHitTestVisible = false };
        holder.Children.Add(badges);
        panel.Child = holder;
        Pump(window);
        var search = ManualCapture.Find<TextBox>(panel, t => t.PlaceholderText == "값 검색");
        var buttons = panel.GetVisualDescendants().OfType<Button>().ToList();
        foreach (var mark in new Mark[]
        {
            new(ManualCapture.Find<TextBlock>(panel, t => t.FontWeight == Avalonia.Media.FontWeight.SemiBold), 1, 1, 0.5, -14),
            new(search, 2, 1, 0.5, -14),
            new(buttons.First(b => b.Content as string == "모두 선택"), 3, 0, 0),
            new(ManualCapture.Find<ListBox>(panel), 4, 1, 0, -14, 14),
            new(kind, 5, 1, 0.5, -30),
            new(value, 6, 1, 0.5, -14),
            new(buttons.First(b => b.Content as string == "적용"), 7, 0, 0),
        })
        {
            Badge(badges, mark);
        }

        Pump(window);
        ManualCapture.Save(window, "result-column-filter");
        host.IsOpen = false;
    });

    // ---- 그리드 편집 ---------------------------------------------------------------------------------------------

    private static async Task<(SqlResultPaneView View, ResultGridControl Grid, GridEditSession Session)> EditProductsAsync(ManualShell shell, Window window)
    {
        var run = await StartRunAsync(shell, ProductsSql, "oranda.exec.runForEdit");
        await FinishAsync(run, window);
        GiveBottomRoom(shell, window);
        var view = ManualCapture.Find<SqlResultPaneView>(window);
        var grid = ManualCapture.Find<ResultGridControl>(view);
        await HeadlessHost.WaitUntil(() => grid.Model is { HasResult: true, RowCount: > 10 } && GridEditBinding.Of(grid.Model) is { CanEdit: true }, 20000);
        var model = grid.Model!;
        var session = GridEditBinding.Of(model)!;
        // 메모리에서만 고친다(반영하지 않는다): 두 번째 상품의 단가.
        Assert.True(GridEditBinding.TrySetCell(model, 1, 3, "25900"));
        model.SelectCell(new GridCell(1, 3));
        Pump(window);
        Assert.True(session.IsDirty);
        return (view, grid, session);
    }

    [Fact]
    public Task Grid_edit_toolbar() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        var (view, grid, _) = await EditProductsAsync(shell, window);
        _ = view;
        _ = grid;
        var overlay = AddOverlay(window);
        view = ManualCapture.Find<SqlResultPaneView>(window);
        grid = ManualCapture.Find<ResultGridControl>(view);
        var model = grid.Model!;

        var lefts = new List<double>();
        var x = grid.RowHeaderWidth;
        foreach (var ordinal in model.VisibleColumns)
        {
            lefts.Add(x);
            x += model.GetColumnWidth(ordinal);
        }

        Point InGrid(double gx, double gy) => grid.TranslatePoint(new Point(gx, gy), overlay) ?? throw new InvalidOperationException("그리드 위치를 알 수 없다.");
        model.SelectCell(new GridCell(4, 1)); // 고친 셀의 노란색이 보이게 다른 셀을 고른다
        Pump(window);
        var rowHead = InGrid(grid.RowHeaderWidth - 6, grid.HeaderHeight + (grid.RowHeight * 1.5)); // 고친 행의 행 머리 칸(✎)
        var cell = InGrid(lefts[3] + model.GetColumnWidth(model.VisibleColumns[3]) - 4, grid.HeaderHeight + (grid.RowHeight * 1.5));
        PlaceBadge(overlay, 7, rowHead.X - 10, rowHead.Y + (grid.RowHeight * 4));
        PlaceBadge(overlay, 8, cell.X + 12, cell.Y);
        var tab = ManualCapture.Find<TextBlock>(window, t => t.Text?.Contains("(편집)", StringComparison.Ordinal) == true);
        SaveWith(window, overlay, "grid-edit-toolbar",
        [
            new Mark(tab, 1, 1, 0.5, 14),
            new Mark(ButtonTagged(view, "oranda.grid.edit.addRow"), 2, 0, 0),
            new Mark(ButtonTagged(view, "oranda.grid.edit.previewDml"), 3, 0, 0),
            new Mark(ButtonTagged(view, "oranda.grid.edit.apply"), 4, 0, 0),
            new Mark(ButtonTagged(view, "oranda.grid.edit.applyAndCommit"), 5, 0, 0),
            new Mark(ButtonTagged(view, "oranda.grid.edit.resetRow"), 6, 0, 0),
        ]);
        ResetEdits(view);
    });

    /// <summary>고친 값을 버린다(DB 에는 아무것도 가지 않았다).</summary>
    private static void ResetEdits(SqlResultPaneView view)
    {
        if (ManualCapture.Find<ResultGridControl>(view).Model is { } model && GridEditBinding.Of(model) is { } session)
        {
            session.ResetAll();
        }
    }

    [Fact]
    public Task Dml_preview_dialog() => HeadlessHost.Run(async () =>
    {
        if (!ManualCapture.Enabled || !SampleDb.Configured)
        {
            return;
        }

        using var shell = await OpenShellAsync();
        var window = shell.Harness.Window;
        var (_, _, session) = await EditProductsAsync(shell, window);
        // 「반영할 DML 미리보기」 와 같은 확인 창을 연다. 닫기(=취소)만 하므로 반영도 커밋도 없다.
        var confirm = GridEditCommands.ConfirmPreview ?? throw new Xunit.Sdk.XunitException("DML 미리보기 연결이 없다.");
        var preview = confirm(session.BuildPreview(), false, CancellationToken.None);
        var dialog = await OpenedDialogAsync(window);
        Assert.StartsWith("DML 미리보기", dialog.Title, StringComparison.Ordinal);
        WidenLeftMargin(dialog);
        var overlay = AddOverlay(dialog);
        SaveWith(dialog, overlay, "dml-preview-dialog",
        [
            new Mark(ManualCapture.Find<TextBlock>(dialog, t => t.Text?.StartsWith("아래 DML", StringComparison.Ordinal) == true), 1, 0, 0.5, -14),
            new Mark(ManualCapture.Find<Oranda.App.Features.SqlEditor.SqlCodeView>(dialog), 2, 0, 0, -14, 10),
            new Mark(ManualCapture.Find<Button>(dialog, b => b.Content as string == "반영(커밋 안 함)"), 3, 0.5, 0, 0, -6),
            new Mark(ManualCapture.Find<Button>(dialog, b => b.Content as string == "취소"), 4, 0.5, 0, 0, -6),
        ]);
        dialog.Close();
        await HeadlessHost.WaitUntil(() => preview.IsCompleted, 10000);
        Assert.False(await preview); // 취소: 아무것도 반영하지 않았다
        session.ResetAll();
    });
}
