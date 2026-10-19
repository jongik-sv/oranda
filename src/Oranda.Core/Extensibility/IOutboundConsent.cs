namespace Oranda.Core.Extensibility;

/// <summary>
/// 외부 전송 전 동의(TRD 4.10). 보낼 내용을 그대로 미리 보여 주고 사용자가 허락할 때만 보낸다.
/// 1.0 사용처: 업데이트 확인, 충돌 보고 전송. 구현: Oranda.App(TSK-10-01, UI 계약 문서).
/// </summary>
public interface IOutboundConsent
{
    Task<ConsentDecision> RequestAsync(OutboundRequest request, CancellationToken ct);
}

public enum OutboundPurpose
{
    UpdateCheck,
    CrashReport,
    Extension,
}

/// <param name="Purpose">전송 목적(「다시 묻지 않음」 기억 단위)</param>
/// <param name="Destination">받는 곳</param>
/// <param name="Summary">한 줄 설명</param>
/// <param name="PayloadPreview">실제로 보낼 내용(텍스트) 그대로</param>
public sealed record OutboundRequest(OutboundPurpose Purpose, Uri Destination, string Summary, string PayloadPreview)
{
    /// <summary>함께 보낼 파일 경로(덤프·로그).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

public enum ConsentDecision
{
    Denied,
    AllowedOnce,
    AllowedAlways,
}
