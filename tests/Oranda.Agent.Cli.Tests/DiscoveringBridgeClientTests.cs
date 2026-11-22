using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli.Tests;

/// <summary>B4: 오래 떠 있는 mcp 가 앱이 다시 켜진 뒤에도 이어지는가.</summary>
public sealed class DiscoveringBridgeClientTests
{
    private static readonly AgentClientInfo Client = new("test", "1");

    [Fact]
    public async Task Target_is_resolved_once_and_the_client_is_reused()
    {
        var resolved = 0;
        var fake = new FakeBridgeClient();
        await using var client = new DiscoveringBridgeClient(() => Target(++resolved), _ => fake);

        await client.CallToolAsync(Client, "a", null, CancellationToken.None);
        await client.CallToolAsync(Client, "b", null, CancellationToken.None);

        Assert.Equal(1, resolved);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task Unreachable_error_drops_the_client_and_the_next_call_finds_again()
    {
        var resolved = new List<BridgeTarget>();
        var first = new FakeBridgeClient { Throw = new BridgeException(AgentErrorCodes.NotRunning, "gone") };
        var second = new FakeBridgeClient();
        var clients = new Queue<FakeBridgeClient>([first, second]);
        await using var client = new DiscoveringBridgeClient(() =>
        {
            var target = Target(resolved.Count + 1);
            resolved.Add(target);
            return target;
        }, _ => clients.Dequeue());

        await Assert.ThrowsAsync<BridgeException>(() => client.ListToolsAsync(Client, CancellationToken.None));
        await client.CallToolAsync(Client, "a", null, CancellationToken.None);

        Assert.Equal(2, resolved.Count);
        Assert.True(first.Disposed);
        Assert.Single(second.Calls);
    }

    [Fact]
    public async Task Resolve_failure_is_raised_and_retried_next_time()
    {
        var attempts = 0;
        var fake = new FakeBridgeClient();
        await using var client = new DiscoveringBridgeClient(
            () => ++attempts == 1 ? throw new BridgeException(AgentErrorCodes.NoEndpoint, "none") : Target(attempts),
            _ => fake);

        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.CallToolAsync(Client, "a", null, CancellationToken.None));
        await client.CallToolAsync(Client, "a", null, CancellationToken.None);

        Assert.Equal(AgentErrorCodes.NoEndpoint, ex.Code);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Denial_keeps_the_client()
    {
        var resolved = 0;
        var fake = new FakeBridgeClient { Throw = new BridgeException(AgentErrorCodes.Level, "no") };
        await using var client = new DiscoveringBridgeClient(() => Target(++resolved), _ => fake);

        await Assert.ThrowsAsync<BridgeException>(() => client.CallToolAsync(Client, "a", null, CancellationToken.None));
        await Assert.ThrowsAsync<BridgeException>(() => client.CallToolAsync(Client, "a", null, CancellationToken.None));

        Assert.Equal(1, resolved);
        Assert.False(fake.Disposed);
    }

    private static BridgeTarget Target(int n) => new($"/tmp/agent-{n}.sock", $"token-{n}", External: true);
}
