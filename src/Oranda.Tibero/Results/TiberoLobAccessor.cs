using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Results;

/// <summary>
/// LOB 전체 읽기(TSK-06-05의 Tibero 판, R3). 전체를 메모리에 올리지 않고 조각(청크)씩 읽는 스트림을 돌려준다.
/// 로케이터 복제·조각 읽기는 같은 연결로 서버에 가므로 세션 명령 잠금(D-041) 안에서 한다: 복제는 여기서, 조각 읽기는 <see cref="SessionGatedStream"/> 이 조각마다 잡는다.
/// 스트림 내용은 <see cref="TiberoLobStreams"/> 가 만든다. 미리보기 길이(<c>InitialLOBFetchSize</c>)는 드라이버 1.1.35 에서 효과가 없어
/// 로케이터에서 앞부분을 읽어 만든다(<see cref="TiberoLobPreview"/>).
/// </summary>
[OrandaService(typeof(ILobAccessor), Dbms = Dbms.Tibero)]
internal sealed class TiberoLobAccessor : ILobAccessor
{
    public async Task<Stream> OpenReadAsync(ISession s, LobValue lob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(lob);
        ct.ThrowIfCancellationRequested();
        if (s is not TiberoSession session)
        {
            return TiberoLobStreams.Open(lob);
        }

        var gate = session.CommandGate;
        await gate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return new SessionGatedStream(TiberoLobStreams.Open(lob), gate);
        }
        finally
        {
            gate.Release();
        }
    }
}
