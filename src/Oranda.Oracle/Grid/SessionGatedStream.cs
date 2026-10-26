using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.Grid;

/// <summary>
/// 서버 LOB 스트림의 읽기·닫기를 세션 명령 잠금(D-041) 안에서 한다. LOB 읽기는 조각마다 같은 연결로 서버 왕복을 하므로
/// 같은 세션의 실행·메타데이터 조회와 섞이지 않게 조각 하나 읽는 동안만 잠금을 쥔다(조각 사이에는 다른 명령이 돈다).
/// </summary>
internal sealed class SessionGatedStream(Stream inner, SessionCommandGate gate) : Stream
{
    private int _disposed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        gate.Enter(CancellationToken.None);
        try
        {
            return inner.Read(buffer, offset, count);
        }
        finally
        {
            gate.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            gate.EnterForCleanup(); // BFILE 닫기 등 서버 왕복이 있다
            try
            {
                inner.Dispose();
            }
            finally
            {
                gate.Release();
            }
        }

        base.Dispose(disposing);
    }
}
