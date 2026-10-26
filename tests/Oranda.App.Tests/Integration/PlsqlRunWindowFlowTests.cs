using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.PlsqlRun;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>TSK-12-02 「프로시저·함수 실행」 창: 실제 명령 처리기 + 실제 의존 + Oracle 로 창을 열어 화면까지 확인한다.</summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class PlsqlRunWindowFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    [Fact]
    public Task Command_opens_window_with_rows_for_procedure_function_and_package() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        db.Exec("create or replace procedure P_RUN_A (p_in in number, p_out out varchar2) is begin p_out := 'v=' || p_in; end;");
        db.Exec("create or replace function F_RUN_A (p_in in number) return number is begin return p_in * 2; end;");
        db.Exec("create or replace procedure P_RUN_NOARG is begin null; end;");
        db.Exec("create or replace function F_RUN_NOARG return number is begin return 1; end;");
        db.Exec("create or replace package PK_RUN_A is procedure hello (p_name in varchar2 default 'x'); function twice (n in number) return number; end;");
        db.Exec("create or replace package body PK_RUN_A is procedure hello (p_name in varchar2 default 'x') is begin null; end; function twice (n in number) return number is begin return n * 2; end; end;");
        var owner = db.User.ToUpperInvariant();
        foreach (var (name, type, expectRows) in new[] { ("P_RUN_A", "PROCEDURE", 2), ("F_RUN_A", "FUNCTION", 2), ("PK_RUN_A", "PACKAGE", 1), ("P_RUN_NOARG", "PROCEDURE", 0), ("F_RUN_NOARG", "FUNCTION", 1) })
        {
            var handler = new PlsqlRunCommandHandler(rig.Container);
            PlsqlRunViewModel? shown = null;
            handler.Presenter = vm => shown = vm;
            await handler.ExecuteAsync(new CommandContext("oranda.plsql.runFromBrowser", FakeContextKeys.AllTrue, new DbObjectRef(owner, name, type)), default);
            Assert.NotNull(shown);
            output.WriteLine($"{name}: rows={shown!.Rows.Count} cand={shown.Candidates.Count} status='{shown.StatusText}' preview={shown.Preview.Length}");
            Assert.Equal(expectRows, shown.Rows.Count);
            Assert.True(shown.CanRun, $"{name}: 실행할 수 없다 — {shown.StatusText}");
        }

        // 편집기 경로(커서가 놓인 패키지 멤버를 요청으로 넘긴다): 그 멤버 하나만 열린다.
        var fromEditor = new PlsqlRunCommandHandler(rig.Container);
        PlsqlRunViewModel? member = null;
        fromEditor.Presenter = vm => member = vm;
        await fromEditor.ExecuteAsync(new CommandContext("oranda.plsql.run", FakeContextKeys.AllTrue, new PlsqlRunRequest(new DbObjectRef(owner, "PK_RUN_A", "PACKAGE BODY"), "twice")), default);
        Assert.NotNull(member);
        Assert.Single(member!.Candidates);
        Assert.Equal(["N"], member.Rows.Where(r => r.CanEdit).Select(r => r.Name));
    });

    [Fact]
    public Task Real_window_shows_controls_and_rows() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        db.Exec("create or replace procedure P_RUN_B (p_in in number, p_out out varchar2) is begin p_out := 'v=' || p_in; end;");
        var owner = db.User.ToUpperInvariant();
        var handler = new PlsqlRunCommandHandler(rig.Container);
        PlsqlRunWindow? shown = null;
        handler.Presenter = vm => shown = PlsqlRunWindow.ShowFor(vm, null);
        await handler.ExecuteAsync(new CommandContext("oranda.plsql.runFromBrowser", FakeContextKeys.AllTrue, new DbObjectRef(owner, "P_RUN_B", "PROCEDURE")), default);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(shown);
        shown!.UpdateLayout();
        output.WriteLine($"window {shown.Bounds} content={shown.Content?.GetType().Name} visual={shown.GetVisualDescendants().Count()}");
        foreach (var t in shown.GetVisualDescendants().OfType<TextBlock>().Take(40))
        {
            output.WriteLine($"  tb '{t.Text}' vis={t.IsVisible} bounds={t.Bounds}");
        }

        Render(shown, "plsqlrun-real.png");
        shown.Close();
    });

    [Fact]
    public Task Full_shell_command_registry_opens_window_over_main_window() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var h = new ShellHarness();
        var service = Oranda.App.Features.ConnectionManager.ConnectionManagerCommands.Resolve(h.Services);
        var profile = new Oranda.Core.Connection.ConnectionProfile("it", db.User, db.DataSource, Id: Guid.NewGuid());
        var outcome = await service.ConnectAsync(profile, db.Password, default);
        Assert.True(outcome.IsConnected, outcome.Message);
        db.Exec("create or replace procedure P_RUN_C (p_in in number, p_out out varchar2) is begin p_out := 'v=' || p_in; end;");
        db.Exec("create or replace procedure P_RUN_D is begin null; end;");
        var owner = db.User.ToUpperInvariant();
        var registry = h.Get<Oranda.Core.Shell.ICommandRegistry>();
        var keys = h.Get<Oranda.Core.Shell.IContextKeys>();
        var view = h.Window.GetVisualDescendants().OfType<Oranda.App.Features.SchemaBrowser.SchemaBrowserView>().First();
        var browser = (Oranda.App.Features.SchemaBrowser.SchemaBrowserViewModel)view.DataContext!;
        await browser.SetSessionAsync(service.ActiveSession!);
        var category = browser.Roots.Single(r => r.Title == owner).Children.Single(c => c.CategoryType == "PROCEDURE");
        await browser.ExpandAsync(category);
        browser.SelectedNode = category.Children.Single(c => c.Ref?.Name == "P_RUN_C");
        Dispatcher.UIThread.RunJobs();
        output.WriteLine("can=" + registry.CanExecute("oranda.plsql.runFromBrowser", keys));
        var before = Avalonia.Application.Current!.ApplicationLifetime;
        var ran = await registry.ExecuteAsync("oranda.plsql.runFromBrowser", keys, null);
        Dispatcher.UIThread.RunJobs();
        output.WriteLine("ran=" + ran + " owned=" + h.Window.OwnedWindows.Count + " lifetime=" + before);
        var win = h.Window.OwnedWindows.OfType<PlsqlRunWindow>().SingleOrDefault();
        Assert.NotNull(win);
        var vm = (PlsqlRunViewModel)win!.DataContext!;
        await HeadlessHost.WaitUntil(() => vm.Rows.Count > 0 || vm.StatusText.Length > 0, 10000);
        output.WriteLine($"rows={vm.Rows.Count} status='{vm.StatusText}' title='{vm.Title}'");
        win.UpdateLayout();
        Render(win, "plsqlrun-shell.png");
        Assert.Equal(2, vm.Rows.Count);
        win.Close();

        // 인자 없는 프로시저: 두 번 연다(두 번째는 메타데이터 캐시를 거친다). 둘 다 빈 목록이 아니라 실행할 수 있는 창이어야 한다.
        for (var i = 0; i < 2; i++)
        {
            browser.SelectedNode = category.Children.Single(c => c.Ref?.Name == "P_RUN_D");
            Dispatcher.UIThread.RunJobs();
            Assert.True(await registry.ExecuteAsync("oranda.plsql.runFromBrowser", keys, null));
            Dispatcher.UIThread.RunJobs();
            var noArg = h.Window.OwnedWindows.OfType<PlsqlRunWindow>().Single();
            var noArgVm = (PlsqlRunViewModel)noArg.DataContext!;
            output.WriteLine($"noarg #{i}: rows={noArgVm.Rows.Count} cand={noArgVm.Candidates.Count} canRun={noArgVm.CanRun} status='{noArgVm.StatusText}'");
            Assert.Empty(noArgVm.Rows);
            Assert.True(noArgVm.CanRun, noArgVm.StatusText);
            Assert.True(await noArgVm.RunAsync(), noArgVm.StatusText);
            noArg.Close();
            Dispatcher.UIThread.RunJobs();
        }

        await service.DisconnectAsync(default);
    });

    private static void Render(Window window, string file)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Width, (int)window.Height));
            bmp.Render(window);
#pragma warning disable CS0618
            bmp.Save(Path.Combine(dir, file));
#pragma warning restore CS0618
        }
    }
}
