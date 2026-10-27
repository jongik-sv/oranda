using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Velopack.Locators;

namespace Oranda.App.Tests.Features.Update;

/// <summary>
/// GitHub 피드는 인증 없는 API(IP 당 시간 60회) 대신 <c>releases/latest/download/</c> 리다이렉트로 확인한다.
/// 로컬 HttpListener 가 GitHub 처럼 <c>latest/download/x</c> → <c>download/v태그/x</c> 로 돌려보낸다.
/// </summary>
public sealed class UpdateFeedHttpTests
{
    [Fact]
    public void GitHub_feed_reads_from_latest_download_not_the_api()
    {
        var feed = UpdateFeedParser.Parse("https://github.com/acme/oranda").Feed!;

        Assert.Equal("https://github.com/acme/oranda/releases/latest/download/", VelopackAppUpdater.SourceLocation(feed));
        Assert.Equal("https://updates.example.com/oranda", VelopackAppUpdater.SourceLocation(UpdateFeedParser.Parse("https://updates.example.com/oranda").Feed!));
    }

    [Fact]
    public async Task Latest_download_redirect_offers_the_newer_version()
    {
        using var server = new FakeReleaseServer();
        var updater = FakeReleaseServer.Updater("0.1.2");

        var offer = await updater.CheckAsync(server.GitHubFeed, CancellationToken.None);

        Assert.NotNull(offer);
        Assert.Equal("0.1.3", offer.Version);
        Assert.Contains(server.Requests, p => p.StartsWith("/acme/oranda/releases/latest/download/releases.", StringComparison.Ordinal));
        Assert.Contains(server.Requests, p => p.StartsWith("/acme/oranda/releases/download/v0.1.3/releases.", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, p => p.Contains("/repos/", StringComparison.Ordinal)); // API 를 부르지 않는다
    }

    [Fact]
    public async Task Same_version_is_up_to_date()
    {
        using var server = new FakeReleaseServer();

        Assert.Null(await FakeReleaseServer.Updater("0.1.3").CheckAsync(server.GitHubFeed, CancellationToken.None));
    }

    [Fact]
    public async Task Pasted_release_address_works_as_a_web_feed()
    {
        using var server = new FakeReleaseServer();
        var feed = new UpdateFeed(UpdateFeedKind.Web, server.Root + "acme/oranda/releases/latest/download");

        var offer = await FakeReleaseServer.Updater("0.1.2").CheckAsync(feed, CancellationToken.None);

        Assert.Equal("0.1.3", offer?.Version);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.RateLimited)]
    [InlineData((HttpStatusCode)429, UpdateFailure.RateLimited)]
    public async Task Http_failures_are_classified(HttpStatusCode status, UpdateFailure expected)
    {
        using var server = new FakeReleaseServer { FailWith = status };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => FakeReleaseServer.Updater("0.1.2").CheckAsync(server.GitHubFeed, CancellationToken.None));

        Assert.Equal(expected, UpdateFailures.Classify(ex));
    }

    [Fact]
    public async Task Connection_refused_is_unreachable()
    {
        var port = FakeReleaseServer.FreePort(); // 아무도 듣지 않는 포트
        var feed = new UpdateFeed(UpdateFeedKind.GitHub, $"http://localhost:{port}/acme/oranda");
        var updater = new VelopackAppUpdater { Locator = FakeReleaseServer.Locator("0.1.2") };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => updater.CheckAsync(feed, CancellationToken.None));

        Assert.Equal(UpdateFailure.Unreachable, UpdateFailures.Classify(ex));
    }

    // ---- 문구(ko/en)·로그 ----

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "요청 한도")]
    [InlineData((HttpStatusCode)429, "요청 한도")]
    [InlineData(HttpStatusCode.NotFound, "찾지 못했습니다")]
    public async Task Manual_check_failure_shows_a_plain_reason_and_keeps_the_raw_text_for_the_tooltip(HttpStatusCode status, string expected)
    {
        var h = new UpdateHarness();
        h.Updater.CheckFails = new HttpRequestException($"Response status code does not indicate success: {(int)status}.", null, status);
        var text = new ShellText(language: "ko");

        await h.Service.CheckAsync(manual: true);

        var line = UpdateTexts.Status(h.Service, text);
        Assert.StartsWith("업데이트를 확인하지 못했습니다: ", line, StringComparison.Ordinal);
        Assert.Contains(expected, line, StringComparison.Ordinal);
        Assert.DoesNotContain("Response status code", line, StringComparison.Ordinal);
        Assert.Contains($"{(int)status}", UpdateTexts.ErrorToolTip(h.Service), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_failure_says_the_server_is_unreachable_in_both_languages()
    {
        var h = new UpdateHarness();
        h.Updater.CheckFails = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known.", new SocketException(11001));

        await h.Service.CheckAsync(manual: true);

        Assert.Contains("닿지 않습니다", UpdateTexts.Status(h.Service, new ShellText(language: "ko")), StringComparison.Ordinal);
        Assert.Contains("could not reach", UpdateTexts.Status(h.Service, new ShellText(language: "en")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such host", UpdateTexts.ErrorToolTip(h.Service), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_failure_keeps_the_original_message()
    {
        var h = new UpdateHarness();
        h.Updater.CheckFails = new InvalidDataException("bad json");

        await h.Service.CheckAsync(manual: true);

        Assert.Equal(UpdateFailure.Other, h.Service.ErrorKind);
        Assert.Contains("bad json", UpdateTexts.Status(h.Service, new ShellText(language: "ko")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_failure_writes_one_log_line_with_the_feed_and_reason()
    {
        var h = new UpdateHarness();
        var marker = "https://updates.example.com/log-" + Guid.NewGuid().ToString("N");
        h.Service.DefaultFeed = marker;
        h.Updater.CheckFails = new HttpRequestException("rate limit exceeded", null, HttpStatusCode.Forbidden);
        using var writer = new StringWriter();
        using var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);
        try
        {
            await h.Service.CheckAsync(manual: false); // 조용한 시작 확인도 로그는 남긴다
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        listener.Flush();
        var line = writer.ToString().Split('\n').Single(l => l.Contains(marker, StringComparison.Ordinal));
        Assert.Contains("업데이트 확인 실패", line, StringComparison.Ordinal);
        Assert.Contains("RateLimited", line, StringComparison.Ordinal);
        Assert.Contains("rate limit exceeded", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_failure_has_a_message_in_both_languages()
    {
        var ko = Oranda.App.Features.Localization.StringCatalog.Load("ko");
        var en = Oranda.App.Features.Localization.StringCatalog.Load("en");
        foreach (var failure in Enum.GetValues<UpdateFailure>().Where(f => f != UpdateFailure.Other))
        {
            var key = UpdateFailures.TextKey(failure);
            Assert.True(ko.ContainsKey(key) && en.ContainsKey(key), key);
        }
    }

    /// <summary>
    /// GitHub 릴리스 흉내: <c>/acme/oranda/releases/latest/download/x</c> → 302 <c>/acme/oranda/releases/download/v0.1.3/x</c>.
    /// 채널 파일(releases.win.json·releases.osx.json 등)은 이름과 무관하게 같은 0.1.3 목록을 돌려준다.
    /// </summary>
    private sealed class FakeReleaseServer : IDisposable
    {
        private const string Feed = """
            {"Assets":[
              {"PackageId":"Oranda","Version":"0.1.3","Type":"Full","FileName":"Oranda-0.1.3-full.nupkg","SHA1":"00","SHA256":"00","Size":100,"NotesMarkdown":"# Oranda 0.1.3"},
              {"PackageId":"Oranda","Version":"0.1.3","Type":"Delta","FileName":"Oranda-0.1.3-delta.nupkg","SHA1":"00","SHA256":"00","Size":10,"NotesMarkdown":"# Oranda 0.1.3"}
            ]}
            """;

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeReleaseServer()
        {
            var port = FreePort();
            Root = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Root);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Root { get; }

        public HttpStatusCode? FailWith { get; init; }

        public ConcurrentQueue<string> Requests { get; } = new();

        public UpdateFeed GitHubFeed => new(UpdateFeedKind.GitHub, Root + "acme/oranda");

        public static VelopackAppUpdater Updater(string installed) => new() { Locator = Locator(installed) };

        public static TestVelopackLocator Locator(string installed) =>
            new TestVelopackLocator("Oranda", installed, Path.Combine(Path.GetTempPath(), "oranda-upd-test-" + Guid.NewGuid().ToString("N")));

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                var path = ctx.Request.Url!.AbsolutePath;
                Requests.Enqueue(path);
                using var response = ctx.Response;
                const string latest = "/acme/oranda/releases/latest/download/";
                if (FailWith is { } fail)
                {
                    response.StatusCode = (int)fail;
                }
                else if (path.StartsWith(latest, StringComparison.Ordinal))
                {
                    response.StatusCode = 302;
                    response.RedirectLocation = "/acme/oranda/releases/download/v0.1.3/" + path[latest.Length..];
                }
                else if (path.StartsWith("/acme/oranda/releases/download/v0.1.3/releases.", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal))
                {
                    var body = Encoding.UTF8.GetBytes(Feed);
                    response.ContentType = "application/json";
                    response.ContentLength64 = body.Length;
                    await response.OutputStream.WriteAsync(body);
                }
                else
                {
                    response.StatusCode = 404;
                }
            }
        }
    }
}
