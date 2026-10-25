using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-10: 「펼친 SQL」 을 아래 도킹 패널에서 MyBatis 쿼리 탭 안 분할로 옮겼다. 세로 분할(오른쪽) 기본·가로 분할(아래) 전환·숨김,
/// 방향·비율 기억, 탭 없이 목록 고르기만 하면 펼친 SQL 이 없음, 도구 창 목록에서 빠짐(결과 창을 덮지 않음), 바인드 창 값 → 디바운스 뒤 다시 계산.
/// </summary>
public sealed partial class ParamsRunTests
{
    private const string ExpandedPanelId = "oranda.panel.mybatisExpanded";

    /// <summary>사용자 매퍼(저장소 밖 파일, 저장소 밖). 없으면 그 시험은 아무것도 하지 않고 끝난다(공개 저장소라 복사하지 않는다).</summary>
    private static string? UserMapperPath => Oranda.App.Tests.TestSupport.UserMapper.Path;

    private static MyBatisStatementEditorView SplitView(Rig rig, SettingsService? settings) =>
        new(rig.Tab, rig.Session, toolbar: null, services: Services(rig.Hub, settings));

    private static MyBatisServiceMap Services(MyBatisEvalHub hub, SettingsService? settings)
    {
        var map = new MyBatisServiceMap().With(hub).With<ICommandRegistry>(new FakeRegistry()).With<IContextKeys>(new AlwaysKeys());
        if (settings is not null)
        {
            map.With(settings);
        }

        return map;
    }

    private static Window ShowWindow(Control content)
    {
        var window = new Window { Width = 1200, Height = 640, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static void Click(Control button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        (TopLevel.GetTopLevel(button) as Window)?.UpdateLayout();
    }

    /// <summary>펼친 SQL 영역의 왼쪽 위가 원문 편집기의 어디에 있는지(창 좌표).</summary>
    private static Point Origin(Visual visual, Visual root) => visual.TranslatePoint(default, root) ?? default;

    [Fact]
    public Task Opening_a_query_tab_shows_its_expanded_sql_on_the_right_and_the_icons_switch_below_and_back() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var settings = new SettingsService(new MemorySettings());
        var view = SplitView(rig, settings);
        var window = ShowWindow(view);
        var split = view.Split;
        var expanded = split.ExpandedView;

        // 기본 = 세로 분할: 원문 칸(머리 줄 · 원문) | 분할선 | 펼친 SQL. 펼친 SQL 이 원문 칸 오른쪽, 같은 높이에서 시작한다.
        Assert.False(split.IsHorizontal);
        Assert.True(split.IsExpandedShown);
        Assert.Equal(3, split.ColumnDefinitions.Count);
        Assert.Empty(split.RowDefinitions);
        Assert.True(split.VerticalButton.IsChecked);
        Assert.False(split.HorizontalButton.IsChecked);
        Assert.True(split.ShowButton.IsChecked);
        var editorAt = Origin(view.EditorPane, window);
        var expandedAt = Origin(expanded, window);
        Assert.True(expandedAt.X > editorAt.X + view.EditorPane.Bounds.Width - 1, $"펼친 SQL x={expandedAt.X}, 원문 칸 끝={editorAt.X + view.EditorPane.Bounds.Width}");
        Assert.Equal(editorAt.Y, expandedAt.Y, 1);

        // 문장 머리 줄과 시나리오 바는 같은 줄: Y·높이가 같고, 원문 편집기는 머리 줄 아래에서 시작한다.
        var header = view.HeaderBar;
        var scenarioBar = expanded.PaneHeader;
        Assert.Equal(Origin(header, window).Y, Origin(scenarioBar, window).Y, 1);
        Assert.Equal(MyBatisExpandedView.PaneHeaderHeight, header.Bounds.Height, 1);
        Assert.Equal(header.Bounds.Height, scenarioBar.Bounds.Height, 1);
        Assert.True(Origin(view.Editor, window).Y >= Origin(header, window).Y + header.Bounds.Height - 1);

        // 시나리오 바 안의 콤보·단추가 잘리지 않는다(아래 테두리 안쪽에 들어간다).
        var barBottom = Origin(scenarioBar, window).Y + scenarioBar.Bounds.Height - scenarioBar.BorderThickness.Bottom;
        var barControls = scenarioBar.GetVisualDescendants().OfType<TemplatedControl>().Where(c => c is ComboBox or Button && c.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(barControls);
        Assert.All(barControls, c => Assert.True(Origin(c, window).Y + c.Bounds.Height <= barBottom + 0.5, $"{c.GetType().Name} 아래 끝={Origin(c, window).Y + c.Bounds.Height}, 바 안쪽 끝={barBottom}"));
        MyBatisFlowRig.Render(window, "split-vertical", 1200, 640); // ORANDA_RENDER_DIR 이 있을 때만 PNG

        // 이 탭의 문장을 지금 값으로 펼친 글이 읽기 전용 편집기에 있다.
        var vm = Assert.IsType<MyBatisExpandedViewModel>(expanded.DataContext);
        Assert.Same(rig.Tab, vm.Tab);
        Assert.True(vm.HasStatement);
        Assert.Equal(rig.Hub.Active!.Result!.Sql, vm.Sql);
        Assert.Equal(vm.Sql, expanded.CodeView.TextEditorControl.Document.Text);
        Assert.Contains("ORDER BY E.ENAME", vm.Sql, StringComparison.Ordinal);

        // 단추 세 개 모두 툴팁(접근 이름)이 있다.
        Assert.All(new[] { split.VerticalButton, split.HorizontalButton, split.ShowButton }, b => Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(b) as string)));

        // 가로 분할 아이콘: 펼친 SQL 이 편집기 아래로 가고 설정에 기억한다.
        Click(split.HorizontalButton);
        await split.LastSave;
        Assert.True(split.IsHorizontal);
        Assert.Equal(3, split.RowDefinitions.Count);
        Assert.Empty(split.ColumnDefinitions);
        Assert.True(split.HorizontalButton.IsChecked);
        Assert.False(split.VerticalButton.IsChecked);
        editorAt = Origin(view.EditorPane, window);
        expandedAt = Origin(expanded, window);
        Assert.True(expandedAt.Y > editorAt.Y + view.EditorPane.Bounds.Height - 1);
        Assert.Equal(editorAt.X, expandedAt.X, 1);
        Assert.True(settings.Current.MyBatis.ExpandedHorizontal);
        MyBatisFlowRig.Render(window, "split-horizontal", 1200, 640);

        // 세로 분할 아이콘으로 되돌림
        Click(split.VerticalButton);
        await split.LastSave;
        Assert.False(split.IsHorizontal);
        Assert.Equal(3, split.ColumnDefinitions.Count);
        Assert.False(settings.Current.MyBatis.ExpandedHorizontal);
        window.Close();
    });

    [Fact]
    public Task A_narrow_source_pane_trims_only_the_header_path_and_keeps_the_chip_and_extra_slot() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var view = SplitView(rig, settings: null);
        view.HeaderExtraSlot.Children.Add(new Button { Classes = { "toolbar" }, Content = "조합 검사" });
        var window = ShowWindow(view);
        var header = view.HeaderBar;
        var path = view.PathBlock;
        var chip = Assert.IsType<Border>(((Grid)path.Parent!).Children[1]);

        // 넓을 때(비율 상한): 경로는 다 보이고 칩이 경로 바로 옆에 붙는다.
        view.Split.SetRatio(MyBatisExpandedSplit.MaxRatio);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.DoesNotContain(path.TextLayout.TextLines, l => l.HasCollapsed);
        var gap = Origin(chip, window).X - (Origin(path, window).X + path.Bounds.Width);
        Assert.InRange(gap, 0, 15);

        // 원문 칸 비율 하한(0.15): 경로만 말줄임하고, 칩과 오른쪽 빈 자리(조합 검사 단추 등)는 머리 줄 안에 온전히 남는다.
        // 원문 칸 폭이 「칩 + 오른쪽 빈 자리 + 40」 이 되도록 창 폭을 정한다: 글꼴(헤드리스 기본·PNG 렌더용 Skia)이 달라도 경로만 모자란 폭이다.
        var needed = chip.Bounds.Width + chip.Margin.Left + view.HeaderExtraSlot.Bounds.Width + header.Padding.Left + header.Padding.Right + 40;
        window.Width = Math.Ceiling((needed / MyBatisExpandedSplit.MinRatio) + MyBatisExpandedSplit.SplitterSize);
        view.Split.SetRatio(MyBatisExpandedSplit.MinRatio);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains(path.TextLayout.TextLines, l => l.HasCollapsed);
        var headerRight = Origin(header, window).X + header.Bounds.Width;
        foreach (var part in new Control[] { chip, view.HeaderExtraSlot })
        {
            Assert.True(part.Bounds.Width > 0, $"{part.GetType().Name} 너비 0");
            Assert.True(Origin(part, window).X + part.Bounds.Width <= headerRight + 0.5, $"{part.GetType().Name} 이 머리 줄 밖으로 나감");
        }

        Assert.True(Origin(chip, window).X + chip.Bounds.Width <= Origin(view.HeaderExtraSlot, window).X + 0.5, "칩이 오른쪽 빈 자리와 겹침");
        MyBatisFlowRig.Render(window, "split-ratio015", (int)window.Width, 640);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_split_remembers_direction_visibility_and_ratio_across_tabs_and_clamps_the_ratio() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var store = new MemorySettings { Saved = new AppSettings { MyBatis = new MyBatisSettings { ExpandedHorizontal = true, ExpandedRatio = 0.3 } } };
        var settings = new SettingsService(store);
        await settings.LoadAsync();

        // 새 탭 View 는 기억한 방향·비율로 시작한다.
        var view = SplitView(rig, settings);
        var window = ShowWindow(view);
        var split = view.Split;
        Assert.True(split.IsHorizontal);
        Assert.Equal(0.3, split.Ratio, 3);
        Assert.Equal(0.3, split.RowDefinitions[0].Height.Value, 3);
        Assert.Equal(0.7, split.RowDefinitions[2].Height.Value, 3);

        // 숨김 토글: 펼친 SQL·분할선이 사라지고 편집기가 다 차지한다. 기억한다.
        Click(split.ShowButton);
        await split.LastSave;
        Assert.False(split.IsExpandedShown);
        Assert.False(split.ExpandedView.IsEffectivelyVisible);
        Assert.False(split.Splitter.IsVisible);
        Assert.Empty(split.RowDefinitions);
        Assert.False(split.VerticalButton.IsChecked);
        Assert.False(split.HorizontalButton.IsChecked);
        Assert.Equal(view.EditorPane.Bounds.Height, split.Bounds.Height, 1);
        Assert.False(store.Saved.MyBatis.ShowExpanded);
        MyBatisFlowRig.Render(window, "split-hidden", 1200, 640);

        // 숨긴 채 다른 탭(새 View)을 열어도 숨김. 방향 아이콘을 누르면 그 방향으로 다시 보인다.
        var other = SplitView(rig, settings);
        Assert.False(other.Split.IsExpandedShown);
        Click(split.VerticalButton);
        await split.LastSave;
        Assert.True(split.IsExpandedShown);
        Assert.False(split.IsHorizontal);
        Assert.True(split.ExpandedView.IsEffectivelyVisible);

        // 비율: 범위 밖·숫자 아님은 자른다(한쪽이 사라지지 않게). 정상 값은 기억한다.
        split.SetRatio(2);
        Assert.Equal(MyBatisExpandedSplit.MaxRatio, split.Ratio);
        split.SetRatio(-1);
        Assert.Equal(MyBatisExpandedSplit.MinRatio, split.Ratio);
        split.SetRatio(double.NaN);
        Assert.Equal(0.5, split.Ratio);
        split.SetRatio(0.62);
        await split.LastSave;
        Assert.Equal(0.62, store.Saved.MyBatis.ExpandedRatio, 3);

        // 다른 탭이 화면에 붙으면(탭을 오가면) 바뀐 설정을 따라간다.
        window.Content = other;
        Dispatcher.UIThread.RunJobs();
        Assert.True(other.Split.IsExpandedShown);
        Assert.False(other.Split.IsHorizontal);
        Assert.Equal(0.62, other.Split.Ratio, 3);
        window.Close();
    });

    [Fact]
    public Task Without_settings_or_hub_the_split_still_works_with_defaults_and_a_failing_save_keeps_the_choice() => Ui.Run(async () =>
    {
        // 서비스 없음(시험·디자이너): 세로 분할 기본, 펼친 SQL 자리에는 안내 한 줄.
        var session = new FakeMyBatisWorkspaceSession();
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "<select id=\"byId\">select 1 from dual</select>");
        session.StatementIds[tab] = "byId";
        var bare = new MyBatisStatementEditorView(tab, session);
        var window = ShowWindow(bare);
        Assert.False(bare.Split.IsHorizontal);
        Assert.True(bare.Split.IsExpandedShown);
        Assert.False(bare.Expanded!.HasStatement);
        Assert.True(bare.Split.Buttons.IsEffectivelyVisible); // 도구 막대가 없어도 분할 단추는 보인다
        Click(bare.Split.HorizontalButton);
        Assert.True(bare.Split.IsHorizontal);
        await bare.Split.LastSave; // 저장할 곳이 없으면 아무것도 안 한다
        window.Close();

        // 설정 파일을 못 쓰면(디스크 오류) 예외 없이 화면은 고른 대로 둔다.
        using var rig = Rig.Create();
        var view = SplitView(rig, new SettingsService(new FailingSettings()));
        window = ShowWindow(view);
        Click(view.Split.HorizontalButton);
        await view.Split.LastSave;
        Assert.True(view.Split.IsHorizontal);
        window.Close();
    });

    [Fact]
    public Task Only_an_open_query_tab_has_expanded_sql_and_picking_in_the_list_alone_shows_none() => Ui.Run(() =>
    {
        // 아래 도킹 패널은 없다: 도구 창 등록·MyBatis 창 보임 규칙 어디에도 없으므로 조회 결과 묶음을 덮지 않는다.
        Assert.Empty(typeof(MyBatisExpandedViewModel).GetCustomAttributes(typeof(OrandaToolPanelAttribute), inherit: false));
        Assert.DoesNotContain(ExpandedPanelId, DockWorkspace.MyBatisToolIds);
        using (var harness = new ShellHarness())
        {
            Assert.Null(harness.Get<IToolPanelRegistry>().Find(ExpandedPanelId));
            Assert.NotNull(harness.Get<IToolPanelRegistry>().Find("oranda.panel.results"));
        }

        using var rig = Rig.Create();
        var view = SplitView(rig, settings: null);
        var window = ShowWindow(view);
        var vm = view.Expanded!;
        Assert.Equal(StatementId, vm.Eval!.StatementId);

        // 「매퍼 쿼리」 창에서 다른 문장을 고르기만 한다(탭은 열지 않는다): 허브의 활성 문장은 바뀌지만
        // 그 문장의 펼친 SQL 은 어디에도 없다 — 열린 탭의 분할은 자기 문장을 그대로 보인다.
        var original = rig.Session.Elements[StatementId];
        rig.Session.Elements["countEmp"] = original with { Id = "countEmp", Text = "<select id=\"countEmp\">SELECT COUNT(*) FROM EMP</select>" };
        rig.Session.SelectedStatementId = "countEmp";
        rig.Hub.Refresh();
        Assert.Equal("countEmp", rig.Hub.Active!.StatementId);
        Assert.Equal(StatementId, vm.Eval!.StatementId);
        Assert.DoesNotContain("COUNT(*)", vm.Sql, StringComparison.Ordinal);
        Assert.Single(window.GetVisualDescendants().OfType<MyBatisExpandedView>());

        // MyBatis 문장 탭이 아닌 탭(일반 SQL)에 붙이면 안내 한 줄뿐이다.
        var plain = MyBatisExpandedViewModel.For(rig.Hub, tab: new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual"));
        Assert.False(plain.HasStatement);
        Assert.Empty(plain.Sql);
        plain.Dispose();

        // 탭을 닫으면(화면에서 떨어지면) 구독을 끊고, 다시 붙으면 처음처럼 따라간다.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Eval);
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(StatementId, vm.Eval!.StatementId);
        Assert.Equal(MyBatisExpandedViewModel.ActionIds.Length, vm.Actions.Count);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_bind_window_value_including_a_substitution_row_changes_the_tab_expanded_sql_after_the_debounce() => Ui.Run(async () =>
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), MyBatisStatementEval.DefaultDebounce);
        using var rig = Rig.Create(debounceMs: 150);
        var view = SplitView(rig, settings: null);
        var window = ShowWindow(view);
        var vm = view.Expanded!;
        var code = view.Split.ExpandedView.CodeView.TextEditorControl;
        Assert.Contains("ORDER BY E.ENAME", vm.Sql, StringComparison.Ordinal);

        // ${} 치환 값도 바인드 변수 창의 「$이름」 행에서 넣는다(분할 영역에는 입력 칸이 없다).
        var codeView = view.Split.ExpandedView.CodeView;
        Assert.Empty(view.Split.ExpandedView.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && !t.GetVisualAncestors().Contains(codeView)));
        Assert.Empty(view.Split.ExpandedView.GetVisualDescendants().OfType<ListBox>());
        RowOf(rig.Tab, "$ORDERBY").Text = "E.SAL DESC";
        Assert.Contains("ORDER BY E.ENAME", vm.Sql, StringComparison.Ordinal); // 디바운스 전에는 그대로
        await HeadlessHost.WaitUntil(() => vm.Sql.Contains("ORDER BY E.SAL DESC", StringComparison.Ordinal));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(vm.Sql, code.Document.Text);

        // 조건 값: 빈 칸 = null 이라 블록이 꺼진다(경계), 값을 넣으면 켜진다.
        RowOf(rig.Tab, "ENAME").Text = string.Empty;
        await HeadlessHost.WaitUntil(() => !vm.Sql.Contains(":ename", StringComparison.Ordinal));
        RowOf(rig.Tab, "ENAME").Text = "한글'이름";
        await HeadlessHost.WaitUntil(() => vm.Sql.Contains(":ename", StringComparison.Ordinal));
        Assert.Contains(Shown(vm), b => b is { Name: ":ename", ValueText: "'한글''이름'" });
        window.Close();
    });

    [Fact]
    public Task The_users_mapper_opens_a_tab_with_the_split_and_bind_values_recompute() => Ui.Run(async () =>
    {
        if (UserMapperPath is not { } userMapper)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀(ORANDA_USER_MAPPER)");
            return;
        }

        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        await host.OpenWorkspaceFileAsync(userMapper, default);
        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        var ids = session.VisibleEntries.Where(e => e.IsStatement && e.CanOpen).Select(e => e.Id!).ToList();
        output.WriteLine($"문장 {ids.Count}개");
        Assert.True(ids.Count > 100);

        var hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), new FakeMyBatisScenarioStore()))
        {
            Host = host,
            Debounce = TimeSpan.FromMilliseconds(40),
            Post = a => a(),
        };
        var settings = new SettingsService(new MemorySettings());

        // 파라미터가 있는 문장 하나를 탭으로 열고 그 분할을 본다.
        var withParams = ids.Where(id => session.ElementOf(id)?.Text.Contains("#{", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(withParams);
        var tab = session.OpenStatement(withParams[0])!;
        Assert.Same(tab, host.Active);
        var view = new MyBatisStatementEditorView(tab, session, toolbar: null, services: Services(hub, settings));
        var window = ShowWindow(view);
        var vm = view.Expanded!;
        await HeadlessHost.WaitUntil(() => vm.Sql.Length > 0);
        Assert.Equal(session.StatementIdOf(tab), vm.Eval!.StatementId);
        Click(view.Split.HorizontalButton);
        Assert.True(view.Split.IsHorizontal);
        Click(view.Split.VerticalButton);
        Assert.False(view.Split.IsHorizontal);

        // 탭을 열지 않은 문장을 고르기만 하면 분할은 그대로 열린 탭의 문장이다.
        var unopened = withParams.Last();
        Assert.NotEqual(vm.Eval.StatementId, unopened);
        session.SelectStatement(unopened);
        hub.Refresh();
        Assert.Equal(session.StatementIdOf(tab), vm.Eval!.StatementId);

        // 바인드 변수 창 값 → 다시 계산(계산 상태 값과 펼친 SQL 이 따라간다)
        var eval = hub.ForTab(tab)!;
        hub.Refresh();
        Assert.NotEmpty(tab.Binds.Rows);
        var changes = 0;
        eval.Changed += (_, _) => changes++;
        var row = tab.Binds.Rows[0];
        row.Text = "X1";
        await HeadlessHost.WaitUntil(() => changes > 0);
        output.WriteLine($"{eval.StatementId}: 행 {tab.Binds.Rows.Count}개, {row.Name}=X1 뒤 SQL {vm.Sql.Length}자");
        Assert.Equal("X1", eval.Values.Values.Select(v => v.Text).FirstOrDefault(t => t == "X1"));
        Assert.Equal(eval.Result?.Sql ?? string.Empty, vm.Sql);

        window.Close();
        hub.Active?.Dispose();
    });

    /// <summary>메모리에만 두는 설정 저장소.</summary>
    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Saved { get; set; } = new();

        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(Saved));

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>쓰기마다 실패하는 설정 저장소(디스크 오류 흉내).</summary>
    private sealed class FailingSettings : ISettingsStore
    {
        public Task<StoredDocument<AppSettings>> LoadAsync(CancellationToken ct) => Task.FromResult(new StoredDocument<AppSettings>(new AppSettings()));

        public Task SaveAsync(AppSettings settings, CancellationToken ct) => throw new IOException("디스크가 가득 찼습니다.");
    }
}
