using Oranda.App.Tests.Features.Ai.AgentCli;
using Oranda.Core.Ai.Gate;

namespace Oranda.App.Tests.Features.Ai.External.Commands;

/// <summary>
/// 끝 왕복: 실제 <c>orandactl ui commands</c>·<c>ui run</c> → 실제 다리·관문 → 명령 처리자 → 실제 레지스트리(가짜 처리기).
/// 거부는 종료 코드 4(<c>command_denied</c>), 모르는 id 는 1(<c>command_not_found</c>).
/// </summary>
[Collection(AgentCliSerial.Name)]
public sealed class CommandCliRoundTripTests
{
    [Fact]
    public async Task Ui_commands_and_ui_run_round_trip_through_orandactl()
    {
        var f = new CommandFixture();
        await using var rig = new AgentCliRig(new IAppToolHandler[] { f.List(), f.Run() });

        var confirm = await rig.RunAsync(["ui", "commands", "--grade", "confirm"]);
        Assert.True(confirm.ExitCode == 0, confirm.ToString());
        Assert.Equal(CommandIds.Save, (string?)Assert.Single(confirm.Json["commands"]!.AsArray())!["id"]);

        var run = await rig.RunAsync(["ui", "run", CommandIds.History]);
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal("done", (string?)run.Json["status"]);
        Assert.Equal(1, f.Handlers[CommandIds.History].Runs);
        Assert.Contains(rig.Audit.Entries, e => e.Tool == "run_command" && e.Reason == "grade=" + (string?)run.Json["grade"] + ",status=done"); // G5 감사 사유

        var denied = await rig.RunAsync(["ui", "run", CommandIds.Commit]);
        Assert.Equal(4, denied.ExitCode);
        Assert.Contains("command_denied", denied.Stdout + denied.Stderr, StringComparison.Ordinal);

        var unknown = await rig.RunAsync(["ui", "run", "oranda.nope"]);
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("command_not_found", unknown.Stdout + unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, f.Handlers[CommandIds.Commit].Runs);
    }

    [Fact]
    public async Task Ui_run_of_a_confirm_command_asks_on_a_tab_and_reports_the_answer()
    {
        var f = new CommandFixture();
        await using var rig = new AgentCliRig(new IAppToolHandler[] { f.Run(confirmWait: TimeSpan.FromMilliseconds(200)) });

        var noTab = await rig.RunAsync(["ui", "run", CommandIds.Save]);
        Assert.NotEqual(0, noTab.ExitCode);
        Assert.Contains("tab_not_found", noTab.Stdout + noTab.Stderr, StringComparison.Ordinal);

        var tab = f.Host.NewDocument();
        var timedOut = await rig.RunAsync(["ui", "run", CommandIds.Save, "--tab", tab.TabId.ToString()]);
        Assert.True(timedOut.ExitCode == 0, timedOut.ToString());
        Assert.Equal("timeout", (string?)timedOut.Json["status"]);
        Assert.Equal("confirm", (string?)timedOut.Json["grade"]);
        Assert.Equal(0, f.Handlers[CommandIds.Save].Runs);
    }
}
