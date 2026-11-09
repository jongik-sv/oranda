namespace Oranda.Tibero.Driver;

/// <summary>
/// 드라이버 CLOB·NCLOB·BLOB 로케이터를 감싼 것. 받은 쪽이 <see cref="Dispose"/> 한다. 스트림이라 한 번에 한 스레드만 쓴다.
/// </summary>
internal sealed class TiberoLob : IDisposable
{
    private readonly Stream _inner;

    private readonly Func<Stream, Stream> _clone;

    private readonly Func<Stream, char[], int, int, int>? _readChars;

    internal TiberoLob(TiberoLobKind kind, Stream inner, Func<Stream, Stream> clone, Func<Stream, char[], int, int, int>? readChars)
    {
        Kind = kind;
        _inner = inner;
        _clone = clone;
        _readChars = readChars;
    }

    public TiberoLobKind Kind { get; }

    public bool IsNClob => Kind == TiberoLobKind.NClob;

    private bool IsBlob => Kind == TiberoLobKind.Blob;

    /// <summary>길이. CLOB 은 UCS-2 바이트(문자 수 = Length/2). 임시 NCLOB 은 0 일 수 있다 — 끝은 Read 가 0 을 줄 때로 본다.</summary>
    public long Length => _inner.Length;

    /// <summary>위치. CLOB 은 바이트(문자 N = 2N)라 홀수면 <see cref="ArgumentException"/>.</summary>
    public long Position
    {
        get => _inner.Position;
        set
        {
            if (!IsBlob && (value & 1) != 0)
            {
                throw new ArgumentException("CLOB 위치는 UCS-2 바이트라 짝수여야 합니다.", nameof(value));
            }

            _inner.Position = value;
        }
    }

    /// <summary>문자를 읽는다(CLOB·NCLOB). BLOB 이면 <see cref="NotSupportedException"/>.</summary>
    public int Read(char[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (IsBlob || _readChars is null)
        {
            throw new NotSupportedException("BLOB 은 바이트로 읽습니다.");
        }

        return _readChars(_inner, buffer, offset, count);
    }

    /// <summary>바이트를 읽는다(BLOB). CLOB 이면 <see cref="NotSupportedException"/>.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!IsBlob)
        {
            throw new NotSupportedException("CLOB 은 문자로 읽습니다.");
        }

        return _inner.Read(buffer, offset, count);
    }

    /// <summary>위치 0 의 새 사본. 따로 <see cref="Dispose"/> 한다.</summary>
    public TiberoLob Clone() => new(Kind, _clone(_inner), _clone, _readChars);

    /// <summary>BLOB 의 드라이버 스트림 그대로(소유는 이 객체). CLOB 이면 <see cref="NotSupportedException"/>.</summary>
    public Stream AsStream() =>
        IsBlob ? _inner : throw new NotSupportedException("CLOB 은 스트림으로 쓸 수 없습니다.");

    public void Dispose() => _inner.Dispose();
}
