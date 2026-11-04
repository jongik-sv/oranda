using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 쿼리 화면 캡처용 셸. 임시 데이터 폴더로 앱 전체를 띄우고, 로컬 샘플 DB(<see cref="SampleDb"/>)에 접속한 뒤 SQL 을 실제로 실행한다.
/// 접속 이름은 <c>sample</c>. 비밀번호는 접속 때만 쓰고 어디에도 저장하지 않는다(안전 저장소에 쓰지 않음).
/// </summary>
internal sealed class ManualShell : IDisposable
{
    private ManualShell(ShellHarness harness) => Harness = harness;

    internal ShellHarness Harness { get; }

    /// <summary>진행 단계를 <c>ORANDA_MANUAL_TRACE</c> 파일에 덧붙인다(멈춘 곳을 찾을 때만 쓴다). 접속 값은 직접 쓰지 않지만 열린 대화상자의 글은 그대로 남으므로 저장소 밖 폴더에서만 쓴다.</summary>
    internal static void Trace(string step)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_MANUAL_TRACE") is { Length: > 0 } file)
        {
            File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + step + "\n");
        }
    }

    internal static SqlEditorHost Host => SqlEditorHost.Current;

    /// <summary>샘플 DB 에 접속한 셸을 띄운다. 접속 값이 없으면 호출 전에 <see cref="SampleDb.Configured"/> 로 거른다.</summary>
    internal static async Task<ManualShell> OpenAsync()
    {
        Trace("harness");
        var harness = new ShellHarness();
        var shell = new ManualShell(harness);
        try
        {
            await ConnectAsync(harness);
        }
        catch
        {
            shell.Dispose(); // 접속에 실패해도 임시 데이터 폴더(접속 프로필)가 남지 않게 한다
            throw;
        }

        return shell;
    }

    /// <summary>접속 없이 셸(서비스)만 띄운다. 설정 창·포매터 프로필처럼 DB 가 필요 없는 화면용.</summary>
    internal static ManualShell OpenOffline() => new(new ShellHarness());

    private static async Task ConnectAsync(ShellHarness harness)
    {
        var wiring = harness.Services.GetService<IEnumerable<IStartupTask>>()!.OfType<SqlEditorDockWiring>().Single();
        Trace("wiring");
        await wiring.RunAsync(default);
        Dispatcher.UIThread.RunJobs();

        var connections = ConnectionManagerCommands.Resolve(harness.Services);
        var profile = new ConnectionProfile("sample", Env("USER"), $"{Env("HOST")}:{Environment.GetEnvironmentVariable("ORANDA_MANUAL_DB_PORT") ?? "1521"}/{Env("SERVICE")}", Group: "예시");
        Trace("connect");
        // 프로필을 먼저 저장해 ID 를 받는다(ID 가 비면 새 탭이 「접속이 열려 있지 않다」 로 보고 확인 창을 띄운다). 비밀번호는 저장하지 않는다.
        profile = await connections.SaveProfileAsync(profile, null, savePassword: false, default);
        var outcome = await connections.ConnectAsync(profile, Env("PASSWORD"), default);
        Trace("connected " + outcome.Status);
        Assert.True(outcome.Status == ConnectStatus.Connected, $"샘플 DB 접속 실패: {outcome.Status}");
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>도구 창 하나의 화면 모델을 찾는다(없으면 null).</summary>
    internal T? Panel<T>(string panelId)
        where T : class
    {
        var stack = new Stack<Dock.Model.Core.IDockable>();
        if (Harness.Get<DockWorkspace>().Layout is { } root)
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            var dockable = stack.Pop();
            if (dockable is PanelTool tool && tool.Definition.Id == panelId)
            {
                return tool.Panel as T;
            }

            foreach (var child in (dockable as Dock.Model.Core.IDock)?.VisibleDockables ?? [])
            {
                stack.Push(child);
            }
        }

        return null;
    }

    /// <summary>스키마 브라우저를 접속한 계정(<c>SAMPLE</c>)의 스키마 하나만 보이게 거른다. 같은 DB 의 다른 계정 이름이 화면에 나오지 않게 한다.</summary>
    internal async Task ShowOnlySampleSchemaAsync()
    {
        var browser = Panel<SchemaBrowserViewModel>("oranda.panel.schemaBrowser") ?? throw new Xunit.Sdk.XunitException("스키마 브라우저를 찾지 못했다.");
        await HeadlessHost.WaitUntil(() => !browser.IsBusy && browser.Roots.Count(r => r.Kind == SchemaNodeKind.Schema) > 2, 60000); // 스키마 목록을 다 읽은 뒤에 거른다
        var filter = new SchemaBrowserFilter { Name = "SAMPLE", Scope = SchemaFilterScope.Selected, Schemas = ["SAMPLE"] };
        await browser.SaveFiltersAsync([filter], filter.Id);
        var done = browser.SelectSavedFilterAsync(filter.Id);
        await HeadlessHost.WaitUntil(() => done.IsCompleted, 30000);
        await done;
        await HeadlessHost.WaitUntil(() => !browser.IsBusy && browser.Roots.Count(r => r.Kind == SchemaNodeKind.Schema) == 1, 60000);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>새 SQL 탭에 <paramref name="sql"/> 을 넣고 「문장 실행」 한다. 결과가 나올 때까지 기다린다.</summary>
    internal async Task<SqlEditorViewModel> RunAsync(string sql, string command = "oranda.exec.runStatement")
    {
        var document = Host.NewDocument(sql);
        Host.Active = document;
        Dispatcher.UIThread.RunJobs();
        var registry = Harness.Get<ICommandRegistry>();
        var keys = Harness.Get<IContextKeys>();
        Trace("run " + command);
        var run = registry.ExecuteAsync(command, keys);
        try
        {
            await HeadlessHost.WaitUntil(() => run.IsCompleted, 20000); // 실행 뒤 화면 갱신이 UI 스레드로 돌아오므로 UI 작업을 처리하며 기다린다
        }
        catch (TimeoutException)
        {
            foreach (var owned in Harness.Window.OwnedWindows)
            {
                var texts = owned.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t));
                Trace($"열린 대화상자: {owned.GetType().Name} '{owned.Title}' [{string.Join(" | ", texts)}]");
            }

            throw;
        }

        Assert.True(await run, $"{command} 를 실행하지 못했다(비활성).");
        Trace("ran");
        Dispatcher.UIThread.RunJobs();
        return document;
    }

    public void Dispose() => Harness.Dispose();

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable("ORANDA_MANUAL_DB_" + name) ?? throw new InvalidOperationException("샘플 DB 환경 변수가 없다: " + name);
}
