using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// 통합 시험 한 건이 쓰는 앱 한 벌: 임시 데이터 폴더의 앱 조립 + 편집기 호스트 + 실행 화면 + 이력 기록기(시작 작업).
/// 실제 Oracle 에 접속하고, 모든 호출은 앱의 명령·서비스를 그대로 거친다. 헤드리스 UI 스레드에서만 쓴다.
/// </summary>
internal sealed class FlowRig : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-itest-" + Guid.NewGuid().ToString("N"));
    private readonly SqlEditorHost _previousHost = SqlEditorHost.Current;
    private readonly IExecutionDialogs _previousDialogs = ExecutionDialogs.Current;

    public FlowRig(ItDb db)
    {
        Db = db;
        Directory.CreateDirectory(_dir);
        Container = AppComposition.Create(new AppCompositionOptions { DataDirectory = _dir });
        Host = new SqlEditorHost();
        SqlEditorHost.Current = Host;
        ExecutionDialogs.Current = Dialogs;
        Service = ConnectionManagerCommands.Resolve(Container);
        Controller = ExecutionCommands.Resolve(Container);
        Handler = (ExecutionCommandHandler)Container.Create(typeof(ExecutionCommandHandler));
    }

    public ItDb Db { get; }

    public ServiceContainer Container { get; }

    public SqlEditorHost Host { get; }

    public FakeDialogs Dialogs { get; } = new();

    public IConnectionService Service { get; }

    public ExecutionController Controller { get; }

    public ExecutionCommandHandler Handler { get; }

    public ExecutionState State => Controller.State;

    public string DataDirectory => _dir;

    public ISession Session => Service.ActiveSession ?? throw new InvalidOperationException("접속이 없다.");

    public ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.Development, bool readOnly = false, string? name = null) =>
        new(name ?? "it-" + env, Db.User, Db.DataSource, Environment: env, ReadOnly: readOnly, Id: Guid.NewGuid());

    /// <summary>이력 기록기(시작 작업)를 돌려 둔다. 접속하기 전에 불러야 세션 이름을 안다.</summary>
    public async Task StartHistoryRecorderAsync()
    {
        Container.ScanDeferred();
        var tasks = Container.GetService<IEnumerable<IStartupTask>>() ?? [];
        var recorder = tasks.Single(t => t.GetType().Name == "HistoryRecorder");
        await recorder.RunAsync(CancellationToken.None);
    }

    public async Task<ConnectOutcome> TryConnectAsync(ConnectionProfile profile, string? password)
    {
        if (Service.ActiveSession is not null)
        {
            await Service.DisconnectAsync(default);
        }

        return await Service.ConnectAsync(profile, password, default);
    }

    public async Task ConnectAsync(ConnectionProfile? profile = null)
    {
        var outcome = await TryConnectAsync(profile ?? Profile(), Db.Password);
        Assert.True(outcome.IsConnected, outcome.Message);
        await HeadlessHost.WaitUntil(() => State.HasSession);
    }

    public Task Run(string commandId) => Handler.ExecuteAsync(new CommandContext(commandId, FakeContextKeys.AllTrue), default);

    public async Task<ResultTabsViewModel> RunSqlAsync(string sql, string command = "oranda.exec.runStatement")
    {
        var tab = Host.NewDocument(sql);
        tab.Select(0, 0);
        await Run(command);
        return (ResultTabsViewModel)tab.ResultPane!;
    }

    public static async Task<string> CellAsync(ResultTabItem item, long row = 0, int col = 0)
    {
        var model = item.Pane!.Model!;
        await model.LoadFirstPageAsync();
        return model.GetCellText(row, col);
    }

    /// <summary>이력 저장소에서 조건을 만족하는 항목이 나타날 때까지 기다린다(기록은 비동기).</summary>
    public async Task<IReadOnlyList<HistoryEntry>> WaitHistoryAsync(Func<IReadOnlyList<HistoryEntry>, bool> ready, int timeoutMs = 10000)
    {
        var store = Container.GetRequiredService<IHistoryStore>();
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var rows = await store.SearchAsync(new HistoryQuery { Limit = 500 }, default);
            if (ready(rows))
            {
                return rows;
            }

            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("이력에 기대한 항목이 나타나지 않았다: " + string.Join(" / ", rows.Select(r => r.SqlText.Trim().Split('\n')[0])));
            }

            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        try
        {
            Service.DisconnectAsync(default).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 정리 중 오류는 무시한다.
        }

        SqlEditorHost.Current = _previousHost;
        ExecutionDialogs.Current = _previousDialogs;
        Container.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
