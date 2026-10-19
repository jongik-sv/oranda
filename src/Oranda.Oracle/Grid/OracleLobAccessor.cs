using Oranda.Core.Composition;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Grid;

/// <summary>LOB 전체 읽기(OracleClob/OracleBlob 스트림). 소유: TSK-06-05. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ILobAccessor))]
internal sealed class OracleLobAccessor : ILobAccessor
{
    public Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-05");
}
