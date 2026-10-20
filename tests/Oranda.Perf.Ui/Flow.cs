using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;

namespace Oranda.Perf.Ui;

/// <summary>
/// DB 시나리오가 함께 쓰는 한 벌: 레인 전용 사용자 → 앱의 접속 서비스로 접속 → 편집기 탭에서 앱의 실행 컨트롤러로 SQL 실행.
/// 모든 호출은 앱이 쓰는 명령·서비스를 그대로 거친다. 끝나면(<see cref="Dispose"/>) 접속을 끊고 레인 사용자를 지운다.
/// </summary>
internal sealed class Flow : IDisposable
{
    private Flow(UiContext ctx, PerfDb db)
    {
        Ctx = ctx;
        Db = db;
        Service = ConnectionManagerCommands.Resolve(ctx.Services);
        Controller = ExecutionCommands.Resolve(ctx.Services);
    }

    public UiContext Ctx { get; }

    public PerfDb Db { get; }

    public IConnectionService Service { get; }

    public ExecutionController Controller { get; }

    public SqlEditorHost Host { get; } = SqlEditorHost.Current;

    public static async Task<Flow> StartAsync(UiContext ctx)
    {
        var db = PerfDb.Create();
        try
        {
            var flow = new Flow(ctx, db);
            var outcome = await flow.Service.ConnectAsync(db.Profile(), db.Password, default);
            if (!outcome.IsConnected)
            {
                throw new InvalidOperationException("접속 실패: " + outcome.Message);
            }

            await UiContext.WaitUntilAsync(() => flow.Controller.State.HasSession, TimeSpan.FromSeconds(10), "실행 상태가 세션을 앎");
            return flow;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>편집기 탭을 열어 화면에 붙을 때까지 기다린다.</summary>
    public async Task<SqlEditorViewModel> OpenTabAsync(string text)
    {
        var tab = Host.NewDocument(text);
        await UiContext.WaitUntilAsync(() => tab.Surface is SqlEditorView, TimeSpan.FromSeconds(10), "편집기 탭이 화면에 붙음");
        return tab;
    }

    /// <summary>탭의 첫 문장을 앱의 실행 컨트롤러로 실행한다(커서 문장 실행 명령과 같은 경로).</summary>
    public async Task<ResultGridModel?> RunAsync(SqlEditorViewModel tab, CancellationToken ct = default)
    {
        tab.Select(0, 0);
        await Controller.RunAsync(RunMode.Statement, tab: tab, ct: ct);
        return ModelOf(tab);
    }

    public static ResultGridModel? ModelOf(SqlEditorViewModel tab) =>
        (tab.ResultPane as ResultTabsViewModel)?.Tabs.LastOrDefault(t => t.Pane is not null)?.Pane?.Model;

    /// <summary>모델을 그리고 있는 그리드 컨트롤을 화면 트리에서 찾는다.</summary>
    public ResultGridControl? FindGrid(ResultGridModel model) =>
        Ctx.Window.GetVisualDescendants().OfType<ResultGridControl>().FirstOrDefault(g => ReferenceEquals(g.Model, model));

    public void Dispose()
    {
        try
        {
            Service.DisconnectAsync(default).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // 정리 중 오류는 무시한다.
        }

        Db.Dispose();
    }
}
