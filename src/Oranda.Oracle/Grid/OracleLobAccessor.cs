using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Composition;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Grid;

/// <summary>
/// LOB 전체 읽기(OracleClob/OracleBlob 스트림, TSK-06-05). 전체를 메모리에 올리지 않고 조각(청크)씩 읽는 스트림을 돌려준다.
/// CLOB 은 UTF-8 로 인코딩한 바이트(문자 쌍 경계가 청크에 걸려도 깨지지 않게 상태를 가진 인코더 사용), BLOB·BFILE 은 원시 바이트.
/// 결과 셀의 <see cref="LobValue.Locator"/> 를 복제해 읽으므로 그리드가 쓰는 로케이터의 위치는 건드리지 않는다.
/// </summary>
[OrandaService(typeof(ILobAccessor))]
internal sealed class OracleLobAccessor : ILobAccessor
{
    public Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(lob);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Open(lob));
    }

    internal static Stream Open(LobValue lob)
    {
        switch (lob.Locator)
        {
            case OracleClob clob:
                {
                    var copy = (OracleClob)clob.Clone();
                    copy.Position = 0;
                    return new ClobUtf8Stream(copy.Read, copy);
                }

            case OracleBlob blob:
                {
                    var copy = (OracleBlob)blob.Clone();
                    copy.Position = 0;
                    return copy;
                }

            case OracleBFile bfile:
                {
                    var copy = (OracleBFile)bfile.Clone();
                    copy.OpenFile();
                    return new BFileStream(copy);
                }

            default:
                throw new InvalidOperationException("이 LOB 은 서버 로케이터가 없어 앞부분 미리보기만 있습니다. 결과를 다시 실행하세요.");
        }
    }

    /// <summary>OracleBFile 은 닫을 때 파일도 닫아야 한다.</summary>
    private sealed class BFileStream(OracleBFile file) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => file.Length;

        public override long Position
        {
            get => file.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);

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
                try
                {
                    file.CloseFile();
                }
                catch (InvalidOperationException)
                {
                    // 이미 닫힘
                }

                file.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
