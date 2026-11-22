using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.External.Commands;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Ai.External.Tune;
using Oranda.App.Shell;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Shell;
using Oranda.App.Features.Ai.External.Results;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Gate;

namespace Oranda.AgentE2e;

/// <summary>
/// <c>--real-editor</c>: 실제 편집기 처리자(C, <see cref="ExternalEditorService"/>)와 실제 <see cref="SqlEditorHost"/> 탭.
/// 편집기 객체는 가짜 UI 스레드 하나에서만 만진다(A5 <c>AgentCliEditorRoundTripTests</c> 와 같은 방식).
/// 사람 대신 0.3초마다 띠를 보고, 제안이 오면 「받아들이기」를 누른다(저장 요청은 누르지 않는다 — 기록만).
/// </summary>
internal sealed class RealEditor : IDisposable
{
    private readonly EventLog _log;
    private readonly SingleThreadUi _ui = new();
    private readonly SqlEditorHost _host;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<object> _seen = [];
    private readonly Task _watcher;
    private RealRun? _run;

    public RealEditor(EventLog log, Guid profileId, params (string Title, string Text)[] tabs)
    {
        _log = log;
        _host = _ui.InvokeAsync(() =>
        {
            var h = new SqlEditorHost(dialogs: new AcceptingDialogs());
            foreach (var (title, text) in tabs)
            {
                var t = h.NewDocument(text);
                t.ConnectionProfileId = profileId;
                t.Title = title;
            }

            return Task.FromResult(h);
        }).GetAwaiter().GetResult();
        Service = new ExternalEditorService { HostSource = () => _host };
        _watcher = Task.Run(WatchAsync);
    }

    public ExternalEditorService Service { get; }

    public IEnumerable<IAppToolHandler> Handlers() =>
    [
        new ListTabsToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new GetTabToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new FocusTabToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new LoggedTool(_log, new NewTabToolHandler(Service) { OnUiThread = _ui.InvokeAsync }, RealToShort),
        new ProposeEditToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new GetProposalToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new SaveTabToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
        new GetBindsToolHandler(Service) { OnUiThread = _ui.InvokeAsync },

        // e-ui 상태·결과 처리자. 앱 창(Avalonia)이 없으니 UI 스레드 넘기기는 그 자리 읽기가 된다. DB 세션이 없어 connected 는 거짓.
        new GetStatusHandler(NoServices.Instance) { Label = () => "agent-e2e" },
        new ListConnectionsHandler(NoServices.Instance),
        new GetResultHandler { Host = _host },
        new GetLastErrorHandler(NoServices.Instance) { Host = _host },
    ];

    /// <summary>
    /// 2단계 실제 처리자: m-ui-cmd 의 <c>list_commands</c>·<c>run_command</c>(실제 <see cref="CommandRegistry"/>·실제 등급표, 명령은 시나리오 목록으로 등록)와
    /// m-skill-docs 의 <c>tune_start</c>(앱 안 AI 튜닝 띄우기만 기록으로 바꿈 — AiTuningLauncher 는 앱 창이 있어야 해서 실앱 확인).
    /// 명령 몸체는 실행되면 <c>command_body</c> 사건(그때 앞 탭)을 남긴다. 확인 띠는 Proposal 띠라 감시가 「받아들이기」를 누른다.
    /// </summary>
    public IEnumerable<IAppToolHandler> Phase2Handlers(
        JsonArray commands, Oranda.Core.Connection.ConnectionProfile profile, FakeResults results,
        Oranda.Core.Language.ILanguageService language, Oranda.Core.Ai.Gate.IStatementReferenceExtractor extractor)
    {
        // w-run-export 실제 run_tab·export_result(실행 경로·가드·내보내기 실제, DB 만 가짜). 편집기 탭과 같은 스레드에서 부른다.
        _run = _ui.InvokeAsync(() => Task.FromResult(new RealRun(_log, _host, profile, results, language, extractor))).GetAwaiter().GetResult();
        IEnumerable<IAppToolHandler> runHandlers = _run.Handlers().Select(h => (IAppToolHandler)new LoggedTool(_log, new OnUi(_ui, h), RealToShort));

        var registry = new CommandRegistry([]);
        foreach (var c in commands.OfType<JsonObject>())
        {
            var id = (string)c["id"]!;
            registry.Register(
                new CommandDefinition
                {
                    Id = id,
                    Title = (string?)c["title"] ?? id,
                    Category = CategoryOf(id),
                    Enablement = (bool?)c["enabled"] == false ? "gridHasResult" : null,
                    Placements = c["menu_path"] is { } path ? ["menu:" + (string)path! + "@10"] : [],
                },
                new LoggingCommand(_log, id, () => _host.Active is { } a ? "t" + (Index(a) + 1) : null));
        }

        var shell = new CommandShell(registry, new ShellText(), new ContextKeyService());
        IAppToolHandler[] handlers =
        [
            new ListCommandsToolHandler(NoServices.Instance) { Shell = () => shell, OnUiThread = _ui.InvokeAsync },
            new RunCommandToolHandler(NoServices.Instance)
            {
                Shell = () => shell,
                OnUiThread = _ui.InvokeAsync,
                Editors = () => _host,
                Settle = () => Task.CompletedTask,
                ConfirmWait = TimeSpan.FromSeconds(30),
            },
            new TuneStartToolHandler(Service)
            {
                OnUiThread = _ui.InvokeAsync,
                Services = () => NoServices.Instance,
                Launch = _ => _log.Write("tune_launch", new JsonObject
                {
                    ["active_tab"] = _host.Active is { } a ? "t" + (Index(a) + 1) : null,
                    ["connection"] = _host.Active?.ConnectionProfileId?.ToString(),
                }),
            },
        ];
        return [.. handlers.Select(h => (IAppToolHandler)new LoggedTool(_log, h, RealToShort)), .. runHandlers];
    }

    /// <summary>처리자를 가짜 UI 스레드에서 부른다(탭·결과 그리드가 그 스레드 것이다).</summary>
    private sealed class OnUi(SingleThreadUi ui, IAppToolHandler inner) : IAppToolHandler
    {
        public string ToolName => inner.ToolName;

        public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
            new(ui.InvokeAsync(() => inner.ExecuteAsync(invocation, ct).AsTask()));
    }

    // 실제 탭 id(Guid) → 판정용 t1·t2 이름. 모르면 그대로.
    private string? RealToShort(string? real) => real is null ? null : _ui.InvokeAsync(() =>
        Task.FromResult(_host.Tabs.FirstOrDefault(t => string.Equals(Short(t), real, StringComparison.OrdinalIgnoreCase)) is { } t ? "t" + (Index(t) + 1) : real))
        .GetAwaiter().GetResult();

    private static CommandCategory CategoryOf(string id) => id.Split('.') is [_, var area, ..] ? area switch
    {
        "file" => CommandCategory.File,
        "conn" => CommandCategory.Connection,
        "edit" => CommandCategory.Edit,
        "format" => CommandCategory.Format,
        "exec" or "txn" => CommandCategory.Sql,
        "grid" => CommandCategory.Result,
        "monitor" => CommandCategory.Monitor,
        "help" => CommandCategory.Help,
        _ => CommandCategory.View,
    }
        : CommandCategory.View;

    private sealed class LoggingCommand(EventLog log, string id, Func<string?> activeTab) : ICommandHandler
    {
        public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            log.Write("command_body", new JsonObject { ["id"] = id, ["active_tab"] = activeTab() });
            return Task.CompletedTask;
        }
    }

    /// <summary>실제 처리자 결과를 판정 사건(ui_run·refused·tune_start)으로 옮긴다. 탭 인자는 t1·t2 이름으로 바꿔 남긴다.</summary>
    private sealed class LoggedTool(EventLog log, IAppToolHandler inner, Func<string?, string?> shortTab) : IAppToolHandler
    {
        public string ToolName => inner.ToolName;

        public async ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct)
        {
            var output = await inner.ExecuteAsync(invocation, ct).ConfigureAwait(false);
            var entry = new JsonObject
            {
                ["tool"] = ToolName,
                ["id"] = (string?)invocation.Arguments[AppFields.Id],
                ["tab"] = shortTab((string?)invocation.Arguments[AppFields.Tab]),
            };
            if (output.IsError)
            {
                entry["status"] = "refused";
                entry["error"] = output.ErrorCode;
                if (output.ErrorCode == AgentErrorCodes.CommandDenied)
                {
                    entry["grade"] = AppToolValues.CommandGrades.Deny;
                }

                log.Write("refused", entry);
            }
            else if (ToolName == AppToolNames.RunCommand)
            {
                entry["status"] = (string?)output.Result?["status"];
                entry["grade"] = (string?)output.Result?["grade"];
                log.Write("ui_run", entry);
            }
            else if (ToolName == AppToolNames.TuneStart)
            {
                log.Write("tune_start", entry);
            }
            else if (ToolName == AppToolNames.RunTab)
            {
                entry["status"] = (string?)output.Result?["status"];
                entry["fetched"] = output.Result?["fetched"]?.DeepClone();
                log.Write("run_tab", entry);
            }
            else if (ToolName == AppToolNames.ExportResult)
            {
                entry["status"] = (string?)output.Result?["status"];
                entry["rows"] = output.Result?["rows"]?.DeepClone();
                entry["out"] = (string?)invocation.Arguments[AppFields.Out];
                log.Write("export", entry);
            }
            else if (ToolName == AppToolNames.NewTab)
            {
                entry["tab_id"] = shortTab((string?)output.Result?["tab_id"]);
                entry["text"] = (string?)invocation.Arguments[AppFields.Text];
                log.Write("new_tab", entry);
            }

            return output;
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    public void Dump() => _ui.InvokeAsync(() =>
    {
        foreach (var t in _host.Tabs)
        {
            // 판정은 t1·t2(만든 순서)로 읽는다. 실제 탭 id 는 real_id 에 남긴다.
            _log.Write("final_tab", new JsonObject { ["tab_id"] = "t" + (Index(t) + 1), ["real_id"] = Short(t), ["title"] = t.Title, ["text"] = t.Text });
        }

        return Task.FromResult(0);
    }).GetAwaiter().GetResult();

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _watcher.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 끄는 중이다.
        }

        _stop.Dispose();
        _run?.Dispose();
        _ui.Dispose();
    }

    private int Index(SqlEditorViewModel tab) => _host.Tabs.IndexOf(tab);

    private static string Short(SqlEditorViewModel tab) => tab.TabId.ToString();

    private async Task WatchAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _ui.InvokeAsync(async () =>
            {
                foreach (var tab in _host.Tabs.ToList())
                {
                    // 화면 흉내: 앱에서는 결과 그리드 컨트롤(ResultGridControl.LoadAsync)이 그려질 때 첫 페이지를 읽는다. 창이 없으니 감시가 대신 읽는다.
                    // run_tab 이 실행 직후 돌려주는 fetched 는 이 흉내보다 앞서므로 0 일 수 있다(앱도 그리기 전이면 같다 — README 「찾은 것」).
                    if (tab.ResultPane is Oranda.App.Features.Execution.ResultTabsViewModel results)
                    {
                        foreach (var model in results.Tabs.Select(r => r.Pane?.Model).OfType<Oranda.App.Features.ResultGrid.ResultGridModel>())
                        {
                            if (model.HasResult && _seen.Add(model))
                            {
                                await model.LoadFirstPageAsync();
                                _log.Write("grid_page", new JsonObject { ["tab_index"] = Index(tab), ["rows"] = model.RowCount });
                            }
                        }
                    }

                    if (tab.AgentBar?.Current is not { } item || !_seen.Add(item))
                    {
                        continue;
                    }

                    var body = new JsonObject { ["tab_index"] = Index(tab), ["title"] = tab.Title, ["bar_kind"] = item.Kind.ToString(), ["headline"] = item.Headline };
                    if (item.Kind == AgentBarItemKind.Proposal && tab.AgentBar.ShowsAccept)
                    {
                        await ((IAsyncRelayCommand)tab.AgentBar.AcceptCommand).ExecuteAsync(null);
                        body["action"] = "accepted";
                        body["text_after"] = tab.Text;
                    }
                    else
                    {
                        body["action"] = "left";
                    }

                    _log.Write("bar", body);
                }

                return 0;
            }).ConfigureAwait(false);
        }
    }

    /// <summary>비교 창에서 언제나 「받아들이기」, 그 밖의 대화는 취소.</summary>
    private sealed class AcceptingDialogs : IEditorDialogs
    {
        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>가짜 UI 스레드(A5 시험대에서 복사).</summary>
    private sealed class SingleThreadUi : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadUi()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "agent-e2e-ui" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public Task<T> InvokeAsync<T>(Func<Task<T>> body)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                async _ =>
                {
                    try
                    {
                        done.SetResult(await body());
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                },
                null);
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
