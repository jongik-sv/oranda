using Oranda.Core.Extensibility;

namespace Oranda.Platform.Diagnostics;

public enum CrashReportSendResult
{
    /// <summary>사용자가 거부했다. 아무것도 보내지 않았다.</summary>
    NotSent,
    Sent,
}

/// <summary>충돌 보고서 전송. 동의(IOutboundConsent)를 받은 뒤에만 전송 함수를 부른다. 소유: TSK-10-01.</summary>
public sealed class CrashReportSender(IOutboundConsent consent, Uri destination, Func<Uri, string, CancellationToken, Task> transport)
{
    public async Task<CrashReportSendResult> SendAsync(string reportPath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(reportPath, ct).ConfigureAwait(false);
        var request = new OutboundRequest(OutboundPurpose.CrashReport, destination, "충돌 보고서 전송", content)
        {
            Attachments = [reportPath],
        };
        var decision = await consent.RequestAsync(request, ct).ConfigureAwait(false);
        if (decision == ConsentDecision.Denied)
        {
            return CrashReportSendResult.NotSent;
        }

        await transport(destination, content, ct).ConfigureAwait(false);
        return CrashReportSendResult.Sent;
    }
}
