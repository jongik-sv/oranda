using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B1: 내장 실행(환경 변수 토큰)과 외부 연결(연결 파일)의 통로 정하기, OS 별 기본 통로.</summary>
public sealed class BridgeTargetResolverTests : IDisposable
{
    private readonly TempDataDir _dir = new();
    private readonly Dictionary<string, string?> _env = [];

    public BridgeTargetResolverTests()
    {
        _env[AppDataLocation.DataDirEnvironmentVariable] = _dir.Path;
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Run_token_in_environment_keeps_the_built_in_path()
    {
        _env[AgentRunToken.EnvironmentVariable] = "run-token";
        _dir.Write(100);

        var target = Resolve(pipe: "oranda-agent-abc");

        Assert.Equal(new BridgeTarget("oranda-agent-abc", "run-token", External: false), target);
    }

    [Fact]
    public void Run_token_without_pipe_uses_the_unix_default_socket_in_the_data_folder()
    {
        _env[AgentRunToken.EnvironmentVariable] = "run-token";

        var target = Resolve(pipe: null);

        Assert.Equal(Path.Combine(_dir.Path, "run", "agent.sock"), target.PipeName);
    }

    [Fact]
    public void Without_run_token_the_endpoint_file_gives_pipe_and_token()
    {
        _dir.Write(100);

        var target = Resolve(pipe: null, alive: 100);

        Assert.Equal(new BridgeTarget("/tmp/agent-100.sock", "token-100", External: true), target);
    }

    [Fact]
    public void Instance_environment_variable_picks_the_window_and_option_wins()
    {
        _dir.Write(100);
        _dir.Write(200);
        _env[EndpointLocator.InstanceEnvironmentVariable] = "200";

        Assert.Equal("token-200", Resolve(null, null, 100, 200).Token);
        Assert.Equal("token-100", Resolve(null, "100", 100, 200).Token);
    }

    [Fact]
    public void Empty_run_token_counts_as_absent()
    {
        _env[AgentRunToken.EnvironmentVariable] = string.Empty;

        Assert.Equal(AgentErrorCodes.NoEndpoint, Assert.Throws<BridgeException>(() => Resolve(null)).Code);
    }

    [Theory]
    [InlineData(AppOs.Linux)]
    [InlineData(AppOs.MacOS)]
    public void Default_pipe_on_unix_is_the_app_socket_path(AppOs os)
    {
        var path = DefaultPipe.Resolve(_env.GetValueOrDefault, os);

        Assert.Equal(AgentPipeName.Unix(_dir.Path), path);
    }

    private BridgeTarget Resolve(string? pipe, string? instance = null, params int[] alive)
    {
        var locator = new EndpointLocator(AppDataLocation.Resolve(_env.GetValueOrDefault, AppOs.Linux), alive.Contains, () => TempDataDir.Now);
        return BridgeTargetResolver.Resolve(pipe, instance, _env.GetValueOrDefault, AppOs.Linux, locator);
    }
}
