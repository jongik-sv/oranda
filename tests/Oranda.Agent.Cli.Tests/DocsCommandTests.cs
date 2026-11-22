using System.Text.Json.Nodes;
using Oranda.Core.Ai.Manual;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli.Tests;

/// <summary>S2 <c>orandactl docs</c>: 앱 없이 매뉴얼 링크 목록(E-27). 다리를 부르지 않는다.</summary>
public sealed class DocsCommandTests : IDisposable
{
    private readonly TempDataDir _dir = new(); // 연결 파일 없음 = Oranda 가 꺼져 있다
    private readonly CliHarness _cli;

    public DocsCommandTests() => _cli = new CliHarness(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Search_lists_manual_links_without_oranda_running()
    {
        var exit = await _cli.RunAsync("docs", "실행", "계획");

        Assert.Equal(AgentCliExit.Success, exit);
        Assert.Empty(_cli.Targets);
        Assert.Empty(_cli.Bridge.Calls);
        var output = _cli.OutJson();
        Assert.Equal("실행 계획", (string?)output["query"]);
        Assert.Equal(OrandaManual.BaseUrl + "/", (string?)output["site"]);
        var urls = output["pages"]!.AsArray().Select(p => (string?)p!["url"]).ToList();
        Assert.Contains(OrandaManual.BaseUrl + "/plan/execution-plan/", urls);
    }

    [Fact]
    public async Task Heading_matches_carry_anchor_links()
    {
        Assert.Equal(AgentCliExit.Success, await _cli.RunAsync("docs", "AI 허용 수준"));

        var page = _cli.OutJson()["pages"]!.AsArray().Single(p => (string?)p!["url"] == OrandaManual.BaseUrl + "/ai/overview/")!;
        var heading = page["headings"]!.AsArray().Single(h => (string?)h!["title"] == "접속의 AI 허용 수준")!;
        Assert.Equal(OrandaManual.BaseUrl + "/ai/overview/#접속의-ai-허용-수준", (string?)heading["url"]);
    }

    [Fact]
    public async Task No_query_lists_every_page_and_a_miss_gives_a_hint_not_an_error()
    {
        Assert.Equal(AgentCliExit.Success, await _cli.RunAsync("docs"));
        var all = _cli.OutJson();
        Assert.Equal(OrandaManual.All.Count, all["pages"]!.AsArray().Count);
        Assert.False((bool)all["more"]!);

        var miss = DocsCommand.Run(new JsonObject { ["query"] = "zzz-없는-기능" });
        Assert.Empty(miss["pages"]!.AsArray());
        Assert.NotNull(miss["hint"]);
    }

    [Fact]
    public void Limit_cuts_the_list_and_reports_more()
    {
        var result = DocsCommand.Run(new JsonObject { ["query"] = string.Empty, ["limit"] = 3 });

        Assert.Equal(3, result["pages"]!.AsArray().Count);
        Assert.True((bool)result["more"]!);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--limit")]
    [InlineData("--limit", "0")]
    public async Task Bad_options_are_usage_errors(params string[] tail)
    {
        Assert.Equal(AgentCliExit.Usage, await _cli.RunAsync(["docs", .. tail]));

        Assert.Equal(AgentErrorCodes.Usage, (string?)_cli.OutJson()["code"]);
        Assert.Empty(_cli.Targets);
    }

    [Fact]
    public async Task Text_mode_prints_readable_links_and_help_lists_docs()
    {
        Assert.Equal(AgentCliExit.Success, await _cli.RunAsync("docs", "외부", "에이전트", "--text"));
        Assert.Contains(OrandaManual.BaseUrl + "/ai/external-agents/", _cli.Out.ToString(), StringComparison.Ordinal);

        Assert.Contains("orandactl docs [query...]", CommandParser.HelpText(), StringComparison.Ordinal);
    }
}
