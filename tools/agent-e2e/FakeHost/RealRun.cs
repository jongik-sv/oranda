using System.Reflection;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Features.Binds;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Testing;

namespace Oranda.AgentE2e;

/// <summary>
/// w-run-export 의 실제 <c>run_tab</c>·<c>export_result</c> 처리자를 실제 편집기 탭에 붙인다.
/// 실행 경로(<see cref="ExecutionController"/>)·문장 가드(<see cref="ExternalSelectGuard"/>)·내보내기(<see cref="DataTransferService"/>, 서버에서 다시 읽음)는 실제,
/// DB 만 가짜: 실행 서비스가 시나리오 결과(<see cref="FakeResults"/>)를 돌려주고 받은 문장·출처를 <c>exec</c> 사건으로 남긴다.
/// </summary>
internal sealed class RealRun : IDisposable
{
    private readonly EventLog _log;

    public RealRun(EventLog log, SqlEditorHost host, ConnectionProfile profile, FakeResults results, ILanguageService language, IStatementReferenceExtractor extractor)
    {
        _log = log;
        var bus = new EventBus();
        var session = new FakeSession(profile);
        var state = new ExecutionState(bus);
        var exec = new FakeExecutionService
        {
            Responder = call =>
            {
                // 가드의 현재 스키마 물음(ExternalSelectGuard.CurrentSchemaSql) = SAMPLE.
                if (call.Statement.Text.Contains("CURRENT_SCHEMA", StringComparison.OrdinalIgnoreCase))
                {
                    return new ExecutionResult(call.Statement, ExecutionStatus.Succeeded)
                    {
                        ResultSet = new FakeResultSet([new Oranda.Core.Grid.ResultColumn(0, "SCHEMA", "VARCHAR2", Oranda.Core.Grid.ColumnDataKind.Text)], [["SAMPLE"]]),
                    };
                }

                var grid = results.Find(call.Statement.Text);
                log.Write("exec", new JsonObject
                {
                    ["sql"] = call.Statement.Text, ["source"] = call.Options?.Source.ToString(), ["rows"] = grid.Rows.Count,
                });
                IReadOnlyList<IReadOnlyList<object?>> rows = [.. grid.Rows.Select(r => (IReadOnlyList<object?>)r.Select((v, i) => grid.Raw(i, v)).ToArray())];
                return new ExecutionResult(call.Statement, ExecutionStatus.Succeeded)
                {
                    ResultSet = new FakeResultSet(grid.Columns, rows, pageSize: 50),
                    Elapsed = TimeSpan.FromMilliseconds(41),
                };
            },
        };
        Controller = new ExecutionController(new ExecutionDeps
        {
            State = state,
            Log = new ExecutionLog(bus, state),
            Dialogs = new NoDialogs(),
            Session = () => session,
            TabSession = _ => session,
            Execution = () => exec,
            Language = () => language,
            Host = () => host,
            Binds = BindVariableStore.For(bus),
        });
        bus.Publish(new SessionOpened(session.Id, profile, session.Server, SessionPurpose.Work));
        Parts = new ExternalRunParts
        {
            Host = host,
            Controller = Controller,
            Guard = new ExternalSelectGuard(language, extractor, null, exec),
            Transfer = new DataTransferService(exec, new FakeMetadataProvider(), Unused<IDataTransferDialect>(), Unused<IImportValueConverter>()),
            OpenFolder = folder =>
            {
                log.Write("open_folder", new JsonObject { ["folder"] = folder });
                return Task.CompletedTask;
            },
        };
    }

    public ExecutionController Controller { get; }

    internal ExternalRunParts Parts { get; }

    public IEnumerable<IAppToolHandler> Handlers() =>
    [
        new RunTabHandler(NoServices.Instance) { Parts = Parts },
        new ExportResultHandler(NoServices.Instance) { Parts = Parts },
    ];

    public void Dispose() => Controller.Dispose();

    private static T Unused<T>()
        where T : class => DispatchProxy.Create<T, Phase2Tools.UnusedProxy>();

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    /// <summary>실행 대화상자는 띄우지 않는다(에이전트 실행은 묻지 않는 길만 탄다). 묻게 되면 거절.</summary>
    private sealed class NoDialogs : IExecutionDialogs
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> last, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) => Task.FromResult(ScriptErrorAction.Stop);

        public Task<UncommittedChoice> AskUncommittedAsync(CancellationToken ct) => Task.FromResult(UncommittedChoice.Cancel);

        public Task<bool> PreviewDmlAsync(string text, CancellationToken ct) => Task.FromResult(false);
    }
}
