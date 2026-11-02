using System.Net;
using System.Net.Sockets;
using System.Text;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Api;

namespace Oranda.Platform.Tests.Ai.Api;

/// <summary>
/// 운영 생성자(<see cref="OpenAiApiDriver(Oranda.Core.Ai.Gate.IAgentGate)"/>)가 만드는 실제 핸들러를 루프백 소켓으로 확인한다:
/// 리다이렉트를 따라가지 않고, 한 서버가 준 쿠키가 같은 호스트의 다른 서버 요청에 실려 가지 않는다.
/// </summary>
public sealed class OpenAiApiLoopbackTests
{
    private static AiLlmProfile ProfileFor(int port) => new()
    {
        Id = Guid.NewGuid(),
        Name = "loopback",
        Preset = AiProfilePreset.Custom,
        Format = AiApiFormat.OpenAi,
        BaseUrl = $"http://127.0.0.1:{port}",
        Model = "m",
    };

    private static AiRequest NewRequest() =>
        new("질문", new AiContext("dev", null, AiAccessLevel.Plan), null, null, null,
            new AgentSession(new AgentRunScope("run-1", new ConnectionProfile("dev", "scott", "h/s"), new AiContext("dev", null, AiAccessLevel.Plan))), null, null);

    private static async Task<List<AiEvent>> RunAsync(OpenAiApiDriver driver, AiLlmProfile profile)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(NewRequest(), new AiRoute(profile, null), CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    // 한 연결에 한 응답을 주고 닫는 아주 작은 HTTP 서버. 받은 요청 머리글을 기록한다.
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<int, string> _respond;
        private readonly Task _loop;

        public TinyServer(Func<int, string> respond)
        {
            _respond = respond;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Requests { get; } = [];

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var lengthMatch = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var already = head.Length - (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                    for (var remaining = (lengthMatch.Success ? int.Parse(lengthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0) - already; remaining > 0;)
                    {
                        var got = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (got == 0)
                        {
                            break;
                        }

                        remaining -= got;
                    }

                    int index;
                    lock (Requests)
                    {
                        Requests.Add(head.ToString());
                        index = Requests.Count - 1;
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(index))).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException or InvalidOperationException)
            {
                // 서버를 닫으면 끝난다.
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }
        }
    }

    private static string Sse(string setCookie)
    {
        var body = string.Concat(
            "data: ", FakeOpenAiServer.Text("ok"), "\n\n",
            "data: ", FakeOpenAiServer.Finish(), "\n\n",
            "data: [DONE]\n\n");
        return "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" + setCookie + "Connection: close\r\n\r\n" + body;
    }

    [Fact]
    public async Task The_production_handler_does_not_follow_a_redirect_to_another_server()
    {
        using var target = new TinyServer(_ => Sse(string.Empty));
        using var origin = new TinyServer(_ => $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/v1/chat/completions\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var driver = new OpenAiApiDriver(new FakeGate());

        var events = await RunAsync(driver, ProfileFor(origin.Port));

        Assert.Equal("api_redirect", Assert.IsType<AiError>(events.Last()).Code);
        Assert.Single(origin.Requests);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task A_cookie_set_by_one_profile_server_is_not_sent_to_another_on_the_same_host()
    {
        using var a = new TinyServer(_ => Sse("Set-Cookie: sid=FROM_A; Path=/\r\n"));
        using var b = new TinyServer(_ => Sse(string.Empty));
        using var driver = new OpenAiApiDriver(new FakeGate());

        Assert.IsType<AiFinal>((await RunAsync(driver, ProfileFor(a.Port))).Last());
        Assert.IsType<AiFinal>((await RunAsync(driver, ProfileFor(b.Port))).Last());
        Assert.IsType<AiFinal>((await RunAsync(driver, ProfileFor(a.Port))).Last());

        Assert.DoesNotContain(b.Requests, r => r.Contains("FROM_A", StringComparison.Ordinal));
        Assert.DoesNotContain(a.Requests, r => r.Contains("sid=", StringComparison.OrdinalIgnoreCase));
    }
}
