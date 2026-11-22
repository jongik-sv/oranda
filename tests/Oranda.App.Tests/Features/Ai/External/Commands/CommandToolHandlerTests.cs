using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Commands;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai.External.Commands;

/// <summary>
/// <c>list_commands</c>·<c>run_command</c> 처리자(2단계 계획 U2·U3). 레지스트리·문맥 키·화면 글은 실제 셸 구현,
/// 명령은 등급표가 아는 실제 id 로 가짜 처리기를 등록한다. 띠는 실제 띠 모델에서 단추를 누른다.
/// </summary>
public sealed class CommandToolHandlerTests
{
    private const string History = CommandIds.History;
    private const string Save = CommandIds.Save;
    private const string Commit = CommandIds.Commit;
    private const string Find = CommandIds.Find;

    private static readonly AgentGrantView Grant = new("g1", [], DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1), ["Claude Code"]);

    private static AppToolInvocation Call(JsonObject? args = null) => new(Grant, "Claude Code", args ?? []);

    private static JsonObject Ok(AgentToolOutput output)
    {
        Assert.False(output.IsError, output.ErrorMessage);
        return Assert.IsType<JsonObject>(output.Result);
    }

    private static void Denied(AgentToolOutput output, string code)
    {
        Assert.True(output.IsError);
        Assert.Equal(code, output.ErrorCode);
    }

    private static JsonObject Entry(JsonObject result, string id) =>
        Assert.Single(result["commands"]!.AsArray(), c => (string?)c!["id"] == id)!.AsObject();

    // ---- list_commands ----

    [Fact]
    public async Task List_gives_id_title_category_menu_path_grade_and_enabled()
    {
        var f = new CommandFixture();
        var result = Ok(await f.List().ExecuteAsync(Call(), CancellationToken.None));

        Assert.Equal(4, result["commands"]!.AsArray().Count);
        var save = Entry(result, Save);
        Assert.Equal("file", (string?)save["category"]);
        Assert.Equal("confirm", (string?)save["grade"]);
        Assert.True((bool)save["enabled"]!);
        Assert.False(string.IsNullOrEmpty((string?)save["title"]));
        Assert.Equal(f.Text.Lookup("Menu.file") + " > " + (string?)save["title"], (string?)save["menu_path"]);

        Assert.Equal(
            f.Text.Lookup("Menu.sql") + " > " + f.Text.Lookup("Menu.sql.txn") + " > " + (string?)Entry(result, Commit)["title"],
            (string?)Entry(result, Commit)["menu_path"]);
        Assert.Equal("deny", (string?)Entry(result, Commit)["grade"]);

        var find = Entry(result, Find);
        Assert.False(find.ContainsKey("menu_path")); // 오른쪽 클릭에만 놓인 명령
        Assert.False((bool)find["enabled"]!); // gridHasResult 거짓

        f.Keys.Set("gridHasResult", true);
        Assert.True((bool)Entry(Ok(await f.List().ExecuteAsync(Call(), CancellationToken.None)), Find)["enabled"]!);
    }

    [Fact]
    public async Task List_filters_by_grade_and_category()
    {
        var f = new CommandFixture();

        var deny = Ok(await f.List().ExecuteAsync(Call(new JsonObject { [AppFields.Grade] = "deny" }), CancellationToken.None));
        Assert.Equal(Commit, (string?)Assert.Single(deny["commands"]!.AsArray())!["id"]);

        var file = Ok(await f.List().ExecuteAsync(Call(new JsonObject { [AppFields.Category] = "File" }), CancellationToken.None));
        Assert.Equal(Save, (string?)Assert.Single(file["commands"]!.AsArray())!["id"]);
    }

    [Theory]
    [InlineData("grade", "maybe")]
    [InlineData("category", "kitchen")]
    [InlineData("category", "3")]
    public async Task List_rejects_unknown_filter_values(string field, string value)
    {
        var f = new CommandFixture();
        Denied(await f.List().ExecuteAsync(Call(new JsonObject { [field] = value }), CancellationToken.None), AgentErrorCodes.InvalidArguments);
    }

    [Fact]
    public async Task Tools_say_not_implemented_when_the_shell_is_not_ready()
    {
        var list = new ListCommandsToolHandler(new EmptyServices()) { OnUiThread = body => body() };
        Denied(await list.ExecuteAsync(Call(), CancellationToken.None), AgentErrorCodes.NotImplemented);
    }

    // ---- run_command ----

    [Fact]
    public async Task Run_now_grade_runs_at_once()
    {
        var f = new CommandFixture();
        var result = Ok(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = History }), CancellationToken.None));

        Assert.Equal("done", (string?)result["status"]);
        Assert.Equal("now", (string?)result["grade"]);
        Assert.Equal(1, f.Handlers[History].Runs);
    }

    [Fact]
    public async Task Run_resolves_aliases_before_grading()
    {
        var f = new CommandFixture();
        f.Add("oranda.monitor.killSession", aliases: ["oranda.view.killAlias"]);

        Assert.Equal("done", (string?)Ok(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.view.oldHistory" }), CancellationToken.None))["status"]);
        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.view.killAlias" }), CancellationToken.None), AgentErrorCodes.CommandDenied);
        Assert.Equal(0, f.Handlers["oranda.monitor.killSession"].Runs);
    }

    [Fact]
    public async Task Run_refuses_unknown_denied_unlisted_and_disabled_commands_without_running_them()
    {
        var f = new CommandFixture();
        f.Add("oranda.view.fromExtension"); // 동적 등록: 바로 접두어 아래지만 표가 모른다

        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.nope" }), CancellationToken.None), AgentErrorCodes.CommandNotFound);
        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Commit }), CancellationToken.None), AgentErrorCodes.CommandDenied);
        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.view.fromExtension" }), CancellationToken.None), AgentErrorCodes.CommandDenied);
        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Find }), CancellationToken.None), AgentErrorCodes.CommandDisabled);

        Assert.All(f.Handlers.Values, h => Assert.Equal(0, h.Runs));
    }

    [Fact]
    public async Task Run_returns_while_a_command_still_waits_on_a_dialog()
    {
        var f = new CommandFixture();
        var dialog = new TaskCompletionSource();
        f.Add("oranda.view.output", body: () => dialog.Task);

        var result = Ok(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.view.output" }), CancellationToken.None));

        Assert.Equal("done", (string?)result["status"]);
        Assert.Equal(1, f.Handlers["oranda.view.output"].Runs);
        dialog.SetResult();
    }

    [Fact]
    public async Task Run_brings_the_tab_forward_first_and_refuses_unknown_tabs()
    {
        var f = new CommandFixture();
        var first = f.Host.NewDocument("SELECT 1 FROM dual");
        var second = f.Host.NewDocument("SELECT 2 FROM dual");
        f.Host.Active = second;

        Ok(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = History, [AppFields.Tab] = first.TabId.ToString() }), CancellationToken.None));
        Assert.Same(first, f.Host.Active);

        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = History, [AppFields.Tab] = Guid.NewGuid().ToString() }), CancellationToken.None), AgentErrorCodes.TabNotFound);
    }

    [Fact]
    public async Task Denied_commands_do_not_move_the_tab()
    {
        var f = new CommandFixture();
        var first = f.Host.NewDocument();
        var second = f.Host.NewDocument();
        f.Host.Active = second;

        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Commit, [AppFields.Tab] = first.TabId.ToString() }), CancellationToken.None), AgentErrorCodes.CommandDenied);
        Assert.Same(second, f.Host.Active);
    }

    [Fact]
    public async Task Confirm_grade_waits_for_the_bar_and_runs_only_when_accepted()
    {
        var f = new CommandFixture();
        var tab = f.Host.NewDocument();
        f.Host.Active = tab;

        var pending = f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Save }), CancellationToken.None).AsTask();
        var bar = Assert.IsType<AgentEditorBarViewModel>(tab.AgentBar);
        Assert.True(bar.IsVisible);
        Assert.Contains(Save, bar.Detail, StringComparison.Ordinal);
        Assert.Equal(0, f.Handlers[Save].Runs);

        await bar.AcceptCommand.ExecuteAsync(null);
        var result = Ok(await pending);

        Assert.Equal("confirmed", (string?)result["status"]);
        Assert.Equal("confirm", (string?)result["grade"]);
        Assert.Equal(1, f.Handlers[Save].Runs);
        Assert.False(bar.IsVisible);
    }

    [Fact]
    public async Task Confirm_grade_is_rejected_from_the_bar_or_times_out_without_running()
    {
        var f = new CommandFixture();
        var tab = f.Host.NewDocument();
        f.Host.Active = tab;

        var rejected = f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Save }), CancellationToken.None).AsTask();
        tab.AgentBar!.DiscardCommand.Execute(null);
        Assert.Equal("rejected", (string?)Ok(await rejected)["status"]);

        var timedOut = Ok(await f.Run(confirmWait: TimeSpan.FromMilliseconds(50)).ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Save }), CancellationToken.None));
        Assert.Equal("timeout", (string?)timedOut["status"]);
        Assert.False(tab.AgentBar!.IsVisible);
        Assert.Equal(0, f.Handlers[Save].Runs);
    }

    [Fact]
    public async Task Confirm_grade_needs_a_tab_to_ask_on()
    {
        var f = new CommandFixture();
        Denied(await f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = Save }), CancellationToken.None), AgentErrorCodes.TabNotFound);
        Assert.Equal(0, f.Handlers[Save].Runs);
    }

    [Fact]
    public async Task Confirmed_command_is_checked_again_after_the_wait()
    {
        var f = new CommandFixture();
        f.Add("oranda.file.close", enablement: "hasDocument");
        f.Keys.Set("hasDocument", true);
        var tab = f.Host.NewDocument();
        f.Host.Active = tab;

        var pending = f.Run().ExecuteAsync(Call(new JsonObject { [AppFields.Id] = "oranda.file.close" }), CancellationToken.None).AsTask();
        f.Keys.Set("hasDocument", false);
        await tab.AgentBar!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal("disabled", (string?)Ok(await pending)["status"]);
        Assert.Equal(0, f.Handlers["oranda.file.close"].Runs);
    }

    [Fact]
    public void Every_run_status_has_contract_text()
    {
        Assert.Equal(
            AppToolValues.CommandStatus.All.Order(StringComparer.Ordinal),
            Enum.GetValues<CommandRunStatus>().Select(RunCommandToolHandler.StatusName).Order(StringComparer.Ordinal));
    }
}
