using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.External.Editor;
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
        new NewTabToolHandler(Service) { OnUiThread = _ui.InvokeAsync },
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
