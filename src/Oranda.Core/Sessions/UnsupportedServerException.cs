namespace Oranda.Core.Sessions;

/// <summary>지원 버전 관문(TRD 4.1, PRD D-06)에서 거부된 서버. 세션은 이미 닫힌 상태로 던진다.</summary>
public sealed class UnsupportedServerException : Exception
{
    public UnsupportedServerException()
    {
    }

    public UnsupportedServerException(string message)
        : base(message)
    {
    }

    public UnsupportedServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnsupportedServerException(string serverVersion, string reason)
        : base($"지원하지 않는 서버 버전 {serverVersion}: {reason}")
    {
        ServerVersion = serverVersion;
        Reason = reason;
    }

    public string? ServerVersion { get; }

    public string? Reason { get; }
}
