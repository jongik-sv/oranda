namespace Oranda.Core.Security;

/// <summary>
/// 접속 비밀(비밀번호)을 메모리에서만 잠시 들고 있는 그릇(data-model 5.1). 직렬화하지 않는다.
/// 사용 후 <see cref="Dispose"/> 로 버퍼를 0으로 지운다.
/// </summary>
public sealed class SecureCredential : IDisposable
{
    private readonly char[] _secret;
    private bool _disposed;

    public SecureCredential(ReadOnlySpan<char> secret)
    {
        _secret = secret.ToArray();
    }

    /// <summary>비밀 문자. <see cref="Dispose"/> 뒤에는 <see cref="ObjectDisposedException"/>.</summary>
    public ReadOnlySpan<char> Secret
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _secret;
        }
    }

    public void Dispose()
    {
        Array.Clear(_secret);
        _disposed = true;
    }
}
