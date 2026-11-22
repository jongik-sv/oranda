using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B1: 연결 파일 찾기와 창 고르기(설계 §3.5, E-5).</summary>
public sealed class EndpointLocatorTests : IDisposable
{
    private readonly TempDataDir _dir = new();
    private readonly HashSet<int> _alive = [];

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void One_live_file_is_chosen_without_instance()
    {
        _dir.Write(100);
        _alive.Add(100);

        var file = Locator().Choose(null);

        Assert.Equal(100, file.Pid);
        Assert.Equal("token-100", file.Token);
    }

    [Fact]
    public void No_file_gives_no_endpoint()
    {
        var ex = Assert.Throws<BridgeException>(() => Locator().Choose(null));

        Assert.Equal(AgentErrorCodes.NoEndpoint, ex.Code);
        Assert.Equal(AgentCliExit.Unreachable, AgentCliExit.For(ex.Code));
    }

    [Fact]
    public void Missing_agents_folder_gives_no_endpoint()
    {
        var locator = new EndpointLocator(Path.Combine(_dir.Path, "nothing-here"), _alive.Contains, () => TempDataDir.Now);

        Assert.Empty(locator.Scan());
        Assert.Equal(AgentErrorCodes.NoEndpoint, Assert.Throws<BridgeException>(() => locator.Choose(null)).Code);
    }

    [Fact]
    public void Dead_pid_files_are_skipped_but_not_deleted()
    {
        _dir.Write(100);
        _dir.Write(200);
        _alive.Add(200);

        var file = Locator().Choose(null);

        Assert.Equal(200, file.Pid);
        Assert.True(File.Exists(AppDataLocation.EndpointFile(_dir.Path, 100)));
    }

    [Fact]
    public void Two_live_files_need_instance_and_list_candidates_without_tokens()
    {
        _dir.Write(100, "작업 A");
        _dir.Write(200, "작업 B");
        _alive.UnionWith([100, 200]);

        var ex = Assert.Throws<BridgeException>(() => Locator().Choose(null));

        Assert.Equal(AgentErrorCodes.MultipleInstances, ex.Code);
        Assert.Equal(AgentCliExit.InstanceChoice, AgentCliExit.For(ex.Code));
        var instances = ex.Details!["instances"]!.AsArray();
        Assert.Equal([100, 200], instances.Select(i => (int)i!["pid"]!));
        Assert.Equal("작업 A", (string?)instances[0]!["label"]);
        Assert.DoesNotContain("token", ex.Details.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("작업 B")]
    public void Instance_picks_by_pid_or_label(string instance)
    {
        _dir.Write(100, "작업 A");
        _dir.Write(200, "작업 B");
        _alive.UnionWith([100, 200]);

        Assert.Equal(200, Locator().Choose(instance).Pid);
    }

    [Fact]
    public void Unknown_instance_gives_instance_not_found_with_the_live_list()
    {
        _dir.Write(100, "작업 A");
        _alive.Add(100);

        var ex = Assert.Throws<BridgeException>(() => Locator().Choose("999"));

        Assert.Equal(AgentErrorCodes.InstanceNotFound, ex.Code);
        Assert.Equal(AgentCliExit.InstanceChoice, AgentCliExit.For(ex.Code));
        Assert.Single(ex.Details!["instances"]!.AsArray());
    }

    [Fact]
    public void Same_label_on_two_windows_is_still_multiple()
    {
        _dir.Write(100, "같은 이름");
        _dir.Write(200, "같은 이름");
        _alive.UnionWith([100, 200]);

        Assert.Equal(AgentErrorCodes.MultipleInstances, Assert.Throws<BridgeException>(() => Locator().Choose("같은 이름")).Code);
    }

    [Fact]
    public void Expired_live_file_gives_grant_expired()
    {
        _dir.Write(100, expiresAt: TempDataDir.Now.AddMinutes(-1));
        _alive.Add(100);

        var ex = Assert.Throws<BridgeException>(() => Locator().Choose(null));

        Assert.Equal(AgentErrorCodes.GrantExpired, ex.Code);
    }

    [Fact]
    public void Expired_file_does_not_block_a_live_one()
    {
        _dir.Write(100, expiresAt: TempDataDir.Now.AddMinutes(-1));
        _dir.Write(200);
        _alive.UnionWith([100, 200]);

        Assert.Equal(200, Locator().Choose(null).Pid);
    }

    [Fact]
    public void Broken_wrong_version_and_misnamed_files_are_skipped()
    {
        var agents = AppDataLocation.AgentsDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(agents, "300.json"), "{ not json");
        var wrongVersion = _dir.Write(301).Serialize().Replace("\"v\": 1", "\"v\": 2", StringComparison.Ordinal);
        File.WriteAllText(AppDataLocation.EndpointFile(_dir.Path, 301), wrongVersion);
        var misnamed = _dir.Write(303);
        File.Move(AppDataLocation.EndpointFile(_dir.Path, 303), Path.Combine(agents, "302.json"));
        File.WriteAllText(Path.Combine(agents, "notes.txt"), misnamed.Serialize());
        _alive.UnionWith([300, 301, 302, 303]);

        Assert.Empty(Locator().Scan());
    }

    [Fact]
    public void Current_process_is_alive_and_a_free_pid_is_not()
    {
        Assert.True(EndpointLocator.IsProcessAlive(Environment.ProcessId));
        Assert.False(EndpointLocator.IsProcessAlive(int.MaxValue));
    }

    private EndpointLocator Locator() => new(_dir.Path, _alive.Contains, () => TempDataDir.Now);
}
