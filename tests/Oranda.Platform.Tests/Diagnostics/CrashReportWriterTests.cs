using Oranda.Core.Extensibility;
using Oranda.Platform.Diagnostics;

namespace Oranda.Platform.Tests.Diagnostics;

// 소유: TSK-10-01. 충돌 보고 저장·동의 후 전송.
public sealed class CrashReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Write_saves_exception_and_masks_password()
    {
        var w = new CrashReportWriter(_dir);
        var path = w.Write(new InvalidOperationException("bad Password=hunter2;User Id=scott"), "1.2.3");
        var text = File.ReadAllText(path);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("1.2.3", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("User Id=scott", text);
    }

    [Fact]
    public void Write_keeps_only_latest_reports()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var w = new CrashReportWriter(_dir, () => t = t.AddSeconds(1));
        for (var i = 0; i < CrashReportWriter.MaxReports + 5; i++)
        {
            w.Write(new InvalidOperationException("x"), "1");
        }

        Assert.Equal(CrashReportWriter.MaxReports, w.List().Count);
    }

    [Fact]
    public async Task Send_does_nothing_when_denied()
    {
        var path = new CrashReportWriter(_dir).Write(new InvalidOperationException("x"), "1");
        var called = false;
        var s = new CrashReportSender(new FixedConsent(ConsentDecision.Denied), new Uri("https://x.test/c"), (_, _, _) => { called = true; return Task.CompletedTask; });
        Assert.Equal(CrashReportSendResult.NotSent, await s.SendAsync(path, CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task Send_posts_previewed_content_when_allowed()
    {
        var path = new CrashReportWriter(_dir).Write(new InvalidOperationException("boom"), "1");
        OutboundRequest? seen = null;
        string? sent = null;
        var s = new CrashReportSender(new FixedConsent(ConsentDecision.AllowedOnce, r => seen = r), new Uri("https://x.test/c"), (_, body, _) => { sent = body; return Task.CompletedTask; });
        Assert.Equal(CrashReportSendResult.Sent, await s.SendAsync(path, CancellationToken.None));
        Assert.Equal(OutboundPurpose.CrashReport, seen!.Purpose);
        Assert.Equal(seen.PayloadPreview, sent);
    }

    private sealed class FixedConsent(ConsentDecision d, Action<OutboundRequest>? on = null) : IOutboundConsent
    {
        public Task<ConsentDecision> RequestAsync(OutboundRequest request, CancellationToken ct)
        {
            on?.Invoke(request);
            return Task.FromResult(d);
        }
    }
}
