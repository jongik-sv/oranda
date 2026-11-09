using Oranda.Core.Execution.Providers;
using Oranda.Core.Grid;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Results;

/// <summary>
/// 결과 셀의 LOB 로케이터(<see cref="LobValue.Locator"/> = <see cref="TiberoLob"/>)를 전체 읽기 스트림으로 연다(R3). 세션을 모르는 부분이라
/// 세션 명령 잠금(D-041)은 부르는 쪽(<c>TiberoLobAccessor</c>)이 <see cref="SessionGatedStream"/> 으로 감싼다.
/// 로케이터를 복제해 읽으므로 그리드가 쥔 로케이터의 위치는 건드리지 않는다. CLOB 은 UTF-8 바이트(문자 쌍이 조각 경계에 걸려도 깨지지 않음),
/// BLOB 은 원시 바이트다. BFILE 은 Tibero 에 없어 지원하지 않는다.
/// </summary>
internal static class TiberoLobStreams
{
    public static Stream Open(LobValue lob)
    {
        ArgumentNullException.ThrowIfNull(lob);
        if (lob.Locator is not TiberoLob locator)
        {
            throw new InvalidOperationException("이 LOB 은 서버 로케이터가 없어 앞부분 미리보기만 있습니다. 결과를 다시 실행하세요.");
        }

        var copy = locator.Clone();
        try
        {
            copy.Position = 0;
            return copy.Kind == TiberoLobKind.Blob ? new BlobStream(copy) : new ClobUtf8Stream(copy.Read, copy);
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    /// <summary>BLOB 사본을 읽기 전용 스트림으로 보인다. 닫으면 사본도 닫는다.</summary>
    private sealed class BlobStream(TiberoLob lob) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => lob.Length;

        public override long Position
        {
            get => lob.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => lob.Read(buffer, offset, count);

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
                lob.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
