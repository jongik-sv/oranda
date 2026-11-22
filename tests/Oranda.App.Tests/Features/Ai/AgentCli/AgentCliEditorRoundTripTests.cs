using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Language;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Language.Completion;
using Oranda.Language.Lint;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 끝 왕복(A5): 실제 <c>orandactl</c> → 실제 다리·관문 → <b>실제 편집기 처리자(C)·언어 처리자(D)</b> → 가짜 편집기 탭.
/// 탭 목록 → 탭 글 → <c>sql check --tab</c> → 제안(<c>--wait 0</c>) → 사람이 띠에서 받아들인 흉내 → <c>tab proposal</c> 로 결과 확인 → 바뀐 글.
/// 편집기 객체는 시험이 만든 한 스레드(가짜 UI 스레드)에서만 만지고, 처리자의 <c>OnUiThread</c> 를 그 스레드로 잇는다.
/// E 의 상태·결과 처리자는 머지 뒤 더한다.
/// </summary>
[Collection(AgentCliSerial.Name)]
public sealed class AgentCliEditorRoundTripTests
{
    private static readonly ConnectionProfile Dev = new("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows);

    [Fact]
    public async Task Tabs_text_check_propose_accept_and_proposal_status_round_trip()
    {
        using var ui = new SingleThreadUi();
        var (host, tab) = await ui.InvokeAsync(() =>
        {
            var h = new SqlEditorHost(dialogs: new AcceptingDialogs());
            var t = h.NewDocument("select * from sample_orders where id = :id");
            t.ConnectionProfileId = Dev.Id;
            t.Title = "주문 조회";
            return Task.FromResult((h, t));
        });
        var service = new ExternalEditorService { HostSource = () => host };
        var handlers = new List<IAppToolHandler>
        {
            new ListTabsToolHandler(service) { OnUiThread = ui.InvokeAsync },
            new GetTabToolHandler(service) { OnUiThread = ui.InvokeAsync },
            new ProposeEditToolHandler(service) { OnUiThread = ui.InvokeAsync },
            new GetProposalToolHandler(service) { OnUiThread = ui.InvokeAsync },
        };
        handlers.Add(new CheckSqlHandler(new LanguageService(), new LintService((MetadataLookup?)null), new TabTextReader(new HandlerList(handlers))));
        await using var rig = new AgentCliRig(handlers, profiles: [Dev]);

        // 1) 탭 목록
        var tabs = await rig.RunAsync(["tabs"], client: "Claude Code");
        Assert.True(tabs.ExitCode == 0, tabs.ToString());
        var listed = tabs.Json["tabs"]!.AsArray().Single()!;
        var id = (string)listed["tab_id"]!;
        Assert.Equal(tab.TabId.ToString(), id);
        Assert.Equal("주문 조회", (string?)listed["title"]);
        Assert.Equal("dev-erp", (string?)listed["connection"]);
        var version = (long)listed["version"]!;

        // 2) 탭 글
        var read = await rig.RunAsync(["tab", "get", "--tab", id]);
        Assert.True(read.ExitCode == 0, read.ToString());
        Assert.Equal("select * from sample_orders where id = :id", (string?)read.Json["text"]);

        // 3) 탭 글 문법 검사(언어 처리자가 get_tab 처리자로 글을 읽는다)
        var check = await rig.RunAsync(["sql", "check", "--tab", id]);
        Assert.True(check.ExitCode == 0, check.ToString());
        Assert.Single(check.Json["statements"]!.AsArray());
        Assert.Contains(check.Json["binds"]!.AsArray(), b => string.Equals((string?)b, "id", StringComparison.OrdinalIgnoreCase));

        // 4) 제안(기다리지 않음)
        const string NewText = "select * from sample_orders where id = :id and stat_cd = 'A'";
        var proposed = await rig.RunAsync(
            ["tab", "propose", "--tab", id, "--base-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture), "--range", "document", "--title", "상태 조건", "--wait", "0", "-"],
            stdin: NewText,
            client: "Claude Code");
        Assert.True(proposed.ExitCode == 0, proposed.ToString());
        Assert.Equal("pending", (string?)proposed.Json["status"]);
        var proposalId = (string)proposed.Json["proposal_id"]!;

        // 5) 사람이 띠에서 받아들인다(비교 창은 그대로 받아들이는 가짜)
        var headline = await ui.InvokeAsync(async () =>
        {
            var bar = tab.AgentBar!;
            var text = bar.Headline;
            await ((IAsyncRelayCommand)bar.AcceptCommand).ExecuteAsync(null);
            return text;
        });
        Assert.Equal("Claude Code 가 편집을 제안했습니다 · 상태 조건", headline);

        // 6) 제안 상태와 바뀐 글
        var status = await rig.RunAsync(["tab", "proposal", proposalId]);
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal("accepted", (string?)status.Json["status"]);
        Assert.True((long)status.Json["version"]! > version);
        Assert.Equal(NewText, await ui.InvokeAsync(() => Task.FromResult(tab.Text)));

        var again = await rig.RunAsync(["tab", "get", "--tab", id]);
        Assert.Equal(NewText, (string?)again.Json["text"]);

        // 낡은 version 으로 다시 제안하면 stale(성공 결과, 종료 코드 0)
        var stale = await rig.RunAsync(["tab", "propose", "--tab", id, "--base-version", version.ToString(System.Globalization.CultureInfo.InvariantCulture), "-"], stdin: "select 1 from dual");
        Assert.Equal(0, stale.ExitCode);
        Assert.Equal("stale", (string?)stale.Json["status"]);
    }

    /// <summary>비교 창에서 언제나 「받아들이기」를 누르는 가짜.</summary>
    private sealed class AcceptingDialogs : IEditorDialogs
    {
        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>언어 처리자가 호출 때 꺼내는 처리자 목록.</summary>
    private sealed class HandlerList(List<IAppToolHandler> handlers) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IAppToolHandler>) ? handlers.ToArray() : null;
    }

    /// <summary>
    /// 가짜 UI 스레드: 스레드 하나와 그 스레드의 동기화 문맥. <c>ConfigureAwait(true)</c> 뒤 이어지는 일도 이 스레드로 돌아온다.
    /// 편집기 객체(AvaloniaEdit 문서)는 만든 스레드에서만 만질 수 있으므로 다리 스레드에서 바로 부르지 않는다.
    /// </summary>
    private sealed class SingleThreadUi : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadUi()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "a5-fake-ui" };
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
