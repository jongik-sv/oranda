using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-01 F3: MyBatis 문장 편집기 View(문서 도구 막대·문장 머리 줄·탭 배지·원문 편집기)와 꾸밈 붙이기.
/// 세션은 실제 열기 훅(<see cref="MyBatisOpenHandler"/>)으로 시안 EmpMapper 를 열어 만든다.
/// </summary>
public sealed class WorkspaceEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbeditor-" + Guid.NewGuid().ToString("N"));

    public WorkspaceEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(SqlEditorHost Host, MyBatisWorkspaceSession Session)> OpenAsync(MyBatisHooks hooks)
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = hooks };
        Assert.True(await new MyBatisOpenHandler().TryOpenAsync(host, path, default));
        return (host, Assert.IsType<MyBatisWorkspaceSession>(Assert.Single(host.Workspaces).MyBatis));
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 900, Height = 600, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static MyBatisStatementEditorView Create(MyBatisEditorViewFactory factory, SqlEditorViewModel tab, MyBatisWorkspaceSession session) =>
        Assert.IsType<MyBatisStatementEditorView>(factory.CreateEditor(tab, session));

    [Fact]
    public Task Factory_view_replaces_the_sql_colorizer_with_the_mybatis_colorizer() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory, editorDecorators: [new MyBatisHighlightDecorator()]));
        var tab = session.OpenStatement("selectEmpList")!;

        var view = Create(factory, tab, session);

        var transformers = view.Editor.TextEditorControl.TextArea.TextView.LineTransformers;
        Assert.DoesNotContain(transformers, t => t is SqlSyntaxColorizer);
        Assert.Single(transformers, t => t is MyBatisSyntaxColorizer);
        Assert.Same(tab, view.DataContext);
        Assert.Same(tab, view.Editor.DataContext);
        Assert.Equal(1, view.AttachedDecorationCount);
    });

    [Fact]
    public Task Resetting_the_tab_keeps_exactly_one_mybatis_colorizer_and_no_sql_colorizer() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory, editorDecorators: [new MyBatisHighlightDecorator()]));
        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);
        var other = session.OpenStatement("insertEmp")!;

        view.DataContext = other; // 안쪽 편집기가 SQL 강조를 새로 만들지만 곧 뗀다

        var transformers = view.Editor.TextEditorControl.TextArea.TextView.LineTransformers;
        Assert.DoesNotContain(transformers, t => t is SqlSyntaxColorizer);
        Assert.Single(transformers, t => t is MyBatisSyntaxColorizer);
        Assert.Same(other, view.Editor.DataContext);
        Assert.Same(other, view.Tab);
    });

    [Fact]
    public Task Focus_slot_of_the_inner_editor_is_mybatisText() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);

        Assert.Equal("mybatisText", FocusSlot.GetName(view.Editor));
        Assert.IsAssignableFrom<IEditorFocusTarget>(view);

        var window = Show(view);
        Assert.Equal("mybatisText", FocusSlot.Find(view.Editor.TextEditorControl));
        window.Close();
    });

    [Fact]
    public Task Tab_badge_letters_follow_the_statement_kind() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));

        foreach (var (id, letters) in new[]
        {
            ("selectEmpList", "SEL"), ("insertEmp", "INS"), ("updateEmpSal", "UPD"), ("deleteEmp", "DEL"), ("empCols", "SQL"),
        })
        {
            var tab = session.OpenStatement(id)!;
            var badge = Assert.IsType<TextBlock>(factory.CreateTabBadge(tab, session));
            Assert.Equal(letters, badge.Text);
            Assert.Equal(10, badge.FontSize);
            Assert.Equal(FontWeight.Bold, badge.FontWeight);
            Assert.NotNull(ToolTip.GetTip(badge));
        }

        Assert.Null(factory.CreateTabBadge(new SqlEditorViewModel(Guid.NewGuid(), "select 1 from dual"), session)); // 이 작업 공간의 문장 탭이 아니다
    });

    [Fact]
    public Task Tab_badge_color_is_the_icon_tone_of_the_statement_kind() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var expected = new Dictionary<string, string>
        {
            ["selectEmpList"] = "OrandaIconPurpleBrush",
            ["insertEmp"] = "OrandaIconGreenBrush",
            ["updateEmpSal"] = "OrandaIconOrangeBrush",
            ["deleteEmp"] = "OrandaIconRedBrush",
            ["empCols"] = "OrandaIconTealBrush",
        };

        foreach (var (id, key) in expected)
        {
            var badge = Assert.IsType<TextBlock>(factory.CreateTabBadge(session.OpenStatement(id)!, session));
            var window = Show(badge);
            Assert.True(badge.TryFindResource(key, badge.ActualThemeVariant, out var resource));
            var wanted = Assert.IsAssignableFrom<ISolidColorBrush>(resource);
            var actual = Assert.IsAssignableFrom<ISolidColorBrush>(badge.Foreground);
            Assert.Equal(wanted.Color, actual.Color);
            window.Close();
        }
    });

    [Fact]
    public Task Header_shows_the_mapper_file_the_bold_statement_id_and_the_dynamic_tag_count() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var tab = session.OpenStatement("selectEmpList")!;
        var view = Create(factory, tab, session);
        var window = Show(view);

        Assert.Equal("EmpMapper.xml › selectEmpList", view.HeaderPathText);
        var runs = view.PathBlock.Inlines!.OfType<Run>().ToList();
        Assert.Equal(FontWeight.Bold, runs[^1].FontWeight);
        Assert.Equal("selectEmpList", runs[^1].Text);
        Assert.Matches(@"\b5\b", view.DynamicTagsChipText); // where + if 3 + foreach (include 는 세지 않는다)

        // 글이 바뀌면 칩이 따라간다.
        var close = tab.Text.LastIndexOf("</select>", StringComparison.Ordinal);
        tab.Document.Insert(close, "<if test=\"x != null\">AND 1 = 1</if>\n");
        Assert.Matches(@"\b6\b", view.DynamicTagsChipText);
        tab.Document.Remove(close, "<if test=\"x != null\">AND 1 = 1</if>\n".Length);
        Assert.Matches(@"\b5\b", view.DynamicTagsChipText);
        window.Close();
    });

    [Fact]
    public Task Header_follows_a_statement_rename() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var tab = session.OpenStatement("selectEmp")!;
        var view = Create(factory, tab, session);
        var window = Show(view);

        session.RenameStatement("selectEmp", "selectEmpOne");

        Assert.Equal("EmpMapper.xml › selectEmpOne", view.HeaderPathText);
        window.Close();
    });

    [Fact]
    public Task Header_leaves_an_empty_slot_on_the_right_for_later_chips() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);

        Assert.Empty(view.HeaderExtraSlot.Children);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, view.HeaderExtraSlot.HorizontalAlignment);
    });

    [Fact]
    public Task Decorators_attach_in_order_and_are_disposed_when_the_view_leaves_the_screen_and_attach_again_when_it_returns() => Ui.Run(async () =>
    {
        var log = new List<string>();
        var contexts = new List<MyBatisEditorContext>();
        var factory = new MyBatisEditorViewFactory();
        var hooks = new MyBatisHooks(
            editorViewFactory: factory,
            editorDecorators: [new SpyDecorator("C", 200, log, contexts), new SpyDecorator("A", 0, log, contexts), new SpyDecorator("B", 100, log, contexts)]);
        var (_, session) = await OpenAsync(hooks);
        var tab = session.OpenStatement("selectEmpList")!;

        var view = Create(factory, tab, session);
        Assert.Equal(["attach A", "attach B", "attach C"], log);
        Assert.All(contexts, c =>
        {
            Assert.Same(session, c.Session);
            Assert.Same(tab, c.Tab);
            Assert.Same(view.Editor.TextEditorControl, c.Editor);
        });

        // 창에 올려도 이미 붙어 있으므로 다시 붙이지 않는다.
        var window = Show(view);
        Assert.Equal(3, log.Count);

        // 화면에서 떨어지면 Dispose.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["attach A", "attach B", "attach C", "dispose C", "dispose B", "dispose A"], log);
        Assert.Equal(0, view.AttachedDecorationCount);

        // 다시 붙으면 새로 붙는다(탭을 오가도 강조가 사라지지 않는다).
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["attach A", "attach B", "attach C"], log.Skip(6));
        Assert.Equal(3, view.AttachedDecorationCount);
        window.Close();
    });

    [Fact]
    public Task Changing_the_tab_disposes_the_old_decorations_and_attaches_new_ones_with_the_new_tab() => Ui.Run(async () =>
    {
        var log = new List<string>();
        var contexts = new List<MyBatisEditorContext>();
        var factory = new MyBatisEditorViewFactory();
        var hooks = new MyBatisHooks(editorViewFactory: factory, editorDecorators: [new SpyDecorator("A", 0, log, contexts)]);
        var (_, session) = await OpenAsync(hooks);
        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);
        var other = session.OpenStatement("deleteEmp")!;

        view.DataContext = other;

        Assert.Equal(["attach A", "dispose A", "attach A"], log);
        Assert.Same(other, contexts[^1].Tab);
        Assert.Equal(1, view.AttachedDecorationCount);
    });

    [Fact]
    public Task A_failing_decorator_does_not_stop_the_other_decorations() => Ui.Run(async () =>
    {
        var log = new List<string>();
        var factory = new MyBatisEditorViewFactory();
        var hooks = new MyBatisHooks(
            editorViewFactory: factory,
            editorDecorators: [new ThrowingDecorator(), new SpyDecorator("B", 100, log, [])]);
        var (_, session) = await OpenAsync(hooks);

        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);

        Assert.Equal(["attach B"], log);
        Assert.Equal(1, view.AttachedDecorationCount);
    });

    [Fact]
    public Task Header_subscriptions_live_only_while_the_view_is_on_screen_and_work_with_any_session() => Ui.Run(() =>
    {
        var session = new FakeMyBatisWorkspaceSession();
        var tab = new SqlEditorViewModel(Guid.NewGuid(), "<select id=\"byId\">select 1 from dual</select>");
        session.StatementIds[tab] = "byId";
        var view = new MyBatisStatementEditorView(tab, session);
        Assert.Equal("UserMapper.xml › byId", view.HeaderPathText);
        Assert.Equal(0, view.AttachedDecorationCount); // 훅 묶음을 얻을 수 없는 세션은 꾸밈 없이 쓴다
        Assert.Equal(0, session.StateChangedSubscribers);

        var window = Show(view);
        Assert.Equal(1, session.StateChangedSubscribers);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, session.StateChangedSubscribers);
        window.Close();
    });

    [Fact]
    public Task Workspace_view_uses_the_factory_for_the_editor_and_the_tab_badge() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory, editorDecorators: [new MyBatisHighlightDecorator()]));
        var window = Show(new SqlWorkspaceView { DataContext = session.Workspace });

        var editor = Assert.Single(window.GetVisualDescendants().OfType<MyBatisStatementEditorView>());
        Assert.Equal("selectEmpList", session.StatementIdOf(editor.Tab));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "SEL");
        Assert.Single(editor.Editor.TextEditorControl.TextArea.TextView.LineTransformers, t => t is MyBatisSyntaxColorizer);
        window.Close();
    });

    [Fact]
    public Task Without_the_shell_services_the_toolbar_is_hidden() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));

        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);

        Assert.Null(view.Toolbar);
        Assert.False(view.ToolbarHost.IsVisible);
    });

    [Fact]
    public Task Container_resolves_the_factory_with_the_shell_services_and_draws_the_toolbar_buttons_in_placement_order() => Ui.Run(async () =>
    {
        using var harness = new ShellHarness();
        var hooks = new MyBatisHooks(harness.Services);
        Assert.IsType<MyBatisEditorViewFactory>(hooks.EditorViewFactory);
        Assert.Contains(hooks.EditorDecorators, d => d is MyBatisHighlightDecorator);

        var (_, session) = await OpenAsync(hooks);
        var view = Assert.IsType<MyBatisStatementEditorView>(hooks.EditorViewFactory!.CreateEditor(session.OpenStatement("selectEmpList")!, session));
        var toolbar = Assert.IsType<MyBatisDocumentToolbar>(view.Toolbar);

        Assert.Equal(
            [
                "oranda.mybatis.runScenario",
                "oranda.mybatis.explainScenario",
                "oranda.mybatis.checkCombinations",
                "oranda.mybatis.save",
                "oranda.mybatis.copyExpanded",
                "oranda.mybatis.copyExpandedWithValues",
            ],
            toolbar.Buttons.Select(b => (string)b.Tag!));

        // 툴팁 = 제목 + 단축키. 저장은 일반 저장(oranda.file.save)의 키를 보인다.
        var registry = harness.Get<ICommandRegistry>();
        var text = harness.Get<IShellText>();
        var keymap = harness.Get<KeymapService>();
        var save = toolbar.Buttons.Single(b => (string)b.Tag! == "oranda.mybatis.save");
        var title = text.Title(registry.Find("oranda.mybatis.save")!);
        var shortcut = keymap.ShortcutText("oranda.file.save");
        Assert.NotNull(shortcut);
        Assert.Equal($"{title} ({KeyBindingTable.ForScreen(shortcut)})", ToolTip.GetTip(save));
    });

    [Fact]
    public Task Toolbar_buttons_follow_the_context_keys_and_run_through_the_registry() => Ui.Run(async () =>
    {
        using var harness = new ShellHarness();
        var registry = harness.Get<ICommandRegistry>();
        var keys = harness.Get<ContextKeyService>();
        var spy = new SpyCommand();
        using var registration = registry.Register(
            new CommandDefinition
            {
                Id = "oranda.test.mybatisSpy",
                Title = "Cmd.oranda.test.mybatisSpy",
                Category = CommandCategory.Sql,
                Icon = "run",
                Enablement = "testSpyEnabled",
                Placements = ["toolbar:mybatis/spy@999"],
            },
            spy);
        var hooks = new MyBatisHooks(harness.Services);
        var (_, session) = await OpenAsync(hooks);
        var view = Assert.IsType<MyBatisStatementEditorView>(hooks.EditorViewFactory!.CreateEditor(session.OpenStatement("selectEmpList")!, session));
        var window = Show(view);
        var button = view.Toolbar!.Buttons.Single(b => (string)b.Tag! == "oranda.test.mybatisSpy");

        // 아직 문맥 키가 없어 꺼져 있고, 켜지면 따라 켜진다.
        Assert.False(button.IsEffectivelyEnabled);
        keys.Set("testSpyEnabled", true);
        await HeadlessHost.WaitUntil(() => button.IsEffectivelyEnabled);

        var command = Assert.IsType<RegistryCommand>(button.Command);
        command.Execute(null);
        await command.LastRun!;
        Assert.Equal(1, spy.Runs);

        keys.Set("testSpyEnabled", false);
        await HeadlessHost.WaitUntil(() => !button.IsEffectivelyEnabled);
        window.Close();
        keys.Set("testSpyEnabled", false);
    });

    [Fact]
    public Task Context_menu_puts_the_mybatis_items_first_then_a_separator_then_the_editor_items() => Ui.Run(() =>
    {
        var saved = ShellContextMenus.Factory;
        try
        {
            var copy = new MenuItem { Header = "copy" };
            var fix = new MenuItem { Header = "autoFix" };
            ShellContextMenus.Factory = surface => surface switch
            {
                "editor" => [copy],
                "mybatisEditor" => [fix],
                _ => [],
            };

            var items = MyBatisEditorContextMenu.Build();

            Assert.Equal(3, items.Count);
            Assert.Same(fix, items[0]);
            Assert.IsType<Separator>(items[1]);
            Assert.Same(copy, items[2]);

            ShellContextMenus.Factory = surface => surface == "editor" ? [copy] : [];
            Assert.Equal([copy], MyBatisEditorContextMenu.Build()); // MyBatis 항목이 없으면 구분선도 없다

            ShellContextMenus.Factory = null;
            Assert.Empty(MyBatisEditorContextMenu.Build());
        }
        finally
        {
            ShellContextMenus.Factory = saved;
        }
    });

    [Fact]
    public Task The_inner_editor_gets_the_merged_context_menu() => Ui.Run(async () =>
    {
        var factory = new MyBatisEditorViewFactory();
        var (_, session) = await OpenAsync(new MyBatisHooks(editorViewFactory: factory));
        var view = Create(factory, session.OpenStatement("selectEmpList")!, session);

        Assert.NotNull(view.Editor.TextEditorControl.ContextMenu);
    });

    private sealed class SpyDecorator(string name, int order, List<string> log, List<MyBatisEditorContext> contexts) : IMyBatisEditorDecorator
    {
        public int Order => order;

        public IDisposable Attach(MyBatisEditorContext context)
        {
            log.Add("attach " + name);
            contexts.Add(context);
            return new Release(() => log.Add("dispose " + name));
        }
    }

    private sealed class ThrowingDecorator : IMyBatisEditorDecorator
    {
        public int Order => 0;

        public IDisposable Attach(MyBatisEditorContext context) => throw new InvalidOperationException("붙일 수 없다");
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class SpyCommand : ICommandHandler
    {
        public int Runs { get; private set; }

        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }
}
