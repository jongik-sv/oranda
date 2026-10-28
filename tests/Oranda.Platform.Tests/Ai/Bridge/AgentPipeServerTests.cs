using System.IO.Pipes;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Tests.Ai.Bridge;

public class AgentPipeServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Round_trips_messages_on_the_fixed_name_and_serves_next_connection_after_close()
    {
        var unique = Guid.NewGuid().ToString("N");
        await using var server = new AgentPipeServer(NameOf(unique), EchoAsync);
        var name = server.Start();
        Assert.Equal($"oranda-test-{unique}", name);

        for (var i = 0; i < 2; i++)
        {
            await using var client = await ConnectAsync(name);
            using var channel = new AgentRpcChannel(client);
            await channel.WriteAsync(AgentRpc.Request(i, "echo", new JsonObject { ["n"] = i }), CancellationToken.None);
            var reply = await ReadAsync(channel);
            Assert.Equal(i, reply!.Id);
            Assert.Equal(i, reply.Result!["n"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Second_server_falls_back_to_pid_suffixed_name()
    {
        var unique = Guid.NewGuid().ToString("N");
        await using var first = new AgentPipeServer(NameOf(unique), EchoAsync);
        await using var second = new AgentPipeServer(NameOf(unique), EchoAsync);

        var firstName = first.Start();
        var secondName = second.Start();

        Assert.Equal($"oranda-test-{unique}", firstName);
        Assert.Equal($"oranda-test-{unique}-{Environment.ProcessId}", secondName);
    }

    [Fact]
    public async Task Line_over_the_limit_closes_only_that_connection()
    {
        var unique = Guid.NewGuid().ToString("N");
        await using var server = new AgentPipeServer(NameOf(unique), EchoAsync);
        var name = server.Start();

        await using (var client = await ConnectAsync(name))
        {
            var oversized = new byte[AgentRpc.MaxMessageBytes + 16];
            Array.Fill(oversized, (byte)'a');
            try
            {
                await client.WriteAsync(oversized);
                await client.FlushAsync();
            }
            catch (IOException)
            {
                // 서버가 먼저 끊어도 괜찮다. 아래에서 끊김을 확인한다.
            }

            using var channel = new AgentRpcChannel(client);
            Assert.Null(await ReadAsync(channel));
        }

        await AssertStillServesAsync(name);
    }

    [Fact]
    public async Task Handler_exception_closes_the_connection_but_server_keeps_running()
    {
        var unique = Guid.NewGuid().ToString("N");
        var calls = 0;
        await using var server = new AgentPipeServer(NameOf(unique), (channel, ct) =>
            Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : EchoAsync(channel, ct));
        var name = server.Start();

        await using (var client = await ConnectAsync(name))
        {
            using var channel = new AgentRpcChannel(client);
            Assert.Null(await ReadAsync(channel));
        }

        await AssertStillServesAsync(name);
    }

    [Fact]
    public async Task Serves_several_connections_at_once_and_reports_when_it_stops()
    {
        var unique = Guid.NewGuid().ToString("N");
        var server = new AgentPipeServer(NameOf(unique), EchoAsync);
        var name = server.Start();
        Assert.True(server.IsRunning);

        await using var a = await ConnectAsync(name);
        await using var b = await ConnectAsync(name);
        using var channelA = new AgentRpcChannel(a);
        using var channelB = new AgentRpcChannel(b);
        await channelA.WriteAsync(AgentRpc.Request(1, "echo", new JsonObject { ["who"] = "a" }), CancellationToken.None);
        await channelB.WriteAsync(AgentRpc.Request(2, "echo", new JsonObject { ["who"] = "b" }), CancellationToken.None);
        Assert.Equal("b", (await ReadAsync(channelB))!.Result!["who"]!.GetValue<string>());
        Assert.Equal("a", (await ReadAsync(channelA))!.Result!["who"]!.GetValue<string>());

        await server.DisposeAsync();
        Assert.False(server.IsRunning);
    }

    private static async Task AssertStillServesAsync(string name)
    {
        await using var next = await ConnectAsync(name);
        using var channel = new AgentRpcChannel(next);
        await channel.WriteAsync(AgentRpc.Request(9, "echo", new JsonObject { ["n"] = 9 }), CancellationToken.None);
        Assert.Equal(9, (await ReadAsync(channel))!.Id);
    }

    private static async Task EchoAsync(AgentRpcChannel channel, CancellationToken ct)
    {
        while (await channel.ReadLineAsync(ct) is { } line)
        {
            var request = AgentRpc.TryParse(line)!;
            await channel.WriteAsync(AgentRpc.Success(request.Id, request.Params), ct);
        }
    }

    private static Func<string, int?, string> NameOf(string unique) =>
        (_, instance) => $"oranda-test-{unique}" + (instance is null ? string.Empty : "-" + instance);

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(Timeout);
        await client.ConnectAsync(cts.Token);
        return client;
    }

    private static async Task<AgentRpcMessage?> ReadAsync(AgentRpcChannel channel)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            var line = await channel.ReadLineAsync(cts.Token);
            return line is null ? null : AgentRpc.TryParse(line);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
