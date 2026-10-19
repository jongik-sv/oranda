using System.Text;

namespace Oranda.Oracle.Grid;

/// <summary>문자 읽기 원천(CLOB)을 UTF-8 바이트 스트림으로 읽는다. 한 번에 <see cref="ChunkChars"/> 문자만 메모리에 둔다.</summary>
/// <param name="readChars">(버퍼, 시작, 개수) → 읽은 문자 수, 끝이면 0</param>
/// <param name="owner">스트림을 닫을 때 같이 닫을 객체</param>
internal sealed class ClobUtf8Stream(Func<char[], int, int, int> readChars, IDisposable? owner, int chunkChars = ClobUtf8Stream.ChunkChars) : Stream
{
    internal const int ChunkChars = 32 * 1024;

    private readonly Encoder _encoder = new UTF8Encoding(false).GetEncoder();
    private readonly char[] _chars = new char[chunkChars];
    private byte[] _pending = [];
    private int _pendingPos;
    private bool _eof;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count == 0)
        {
            return 0;
        }

        while (_pendingPos >= _pending.Length)
        {
            if (_eof)
            {
                return 0;
            }

            var read = readChars(_chars, 0, _chars.Length);
            if (read <= 0)
            {
                _eof = true;
                _pending = [];
                _pendingPos = 0;
                return 0;
            }

            var n = _encoder.GetByteCount(_chars, 0, read, flush: false);
            _pending = new byte[n];
            _encoder.GetBytes(_chars, 0, read, _pending, 0, flush: false);
            _pendingPos = 0;
        }

        var take = Math.Min(count, _pending.Length - _pendingPos);
        Buffer.BlockCopy(_pending, _pendingPos, buffer, offset, take);
        _pendingPos += take;
        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            owner?.Dispose();
        }

        base.Dispose(disposing);
    }
}
