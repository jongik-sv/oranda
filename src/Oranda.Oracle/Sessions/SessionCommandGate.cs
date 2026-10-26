using Oranda.Core.Sessions;

namespace Oranda.Oracle.Sessions;

/// <summary>
/// 세션 연결 하나에 한 번에 한 명령만 보내게 하는 잠금(D-041). <see cref="OracleSession"/> 이 하나 가지고, 그 연결을 쓰는 모든 기능
/// (실행·결과 페이지·OUT 커서·커밋·롤백·Keep Alive·메타데이터·DDL·계획·튜닝·모니터·LOB 읽기)이 이 잠금 하나를 쥔 동안에만 서버에 명령을 보낸다.
/// ODP.NET 연결은 스레드 안전하지 않고, 취소(브레이크)는 연결 단위라 잠금을 쥔 명령에만 닿아야 한다.
/// <para>
/// 잡는 단위는 서버 명령 한 덩어리다. 결과 집합은 실행이 끝나면 잠금을 놓고 페이지마다 다시 잡는다(열린 커서가 있어도 다른 명령은 그 사이에 돈다).
/// 재진입하지 않는다: 잠금을 쥔 흐름이 같은 세션의 잠금을 다시 잡는 길(예: 실행 안에서 서비스 API 호출)을 만들면 교착이다.
/// </para>
/// <para>
/// 기다림: 기본은 차례가 올 때까지(취소 토큰으로 그만둠). <see cref="SessionWait.Brief"/> 범위 안이면 한도까지만 기다리고
/// <see cref="SessionBusyException"/>. Keep Alive 는 <see cref="TryEnter"/> 로 바쁘면 건너뛴다.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim 은 대기 핸들을 만들지 않으면 정리할 자원이 없고, 세션이 닫힌 뒤 늦게 끝나는 작업이 놓을 수 있게 둔다.")]
internal sealed class SessionCommandGate
{
    // 세션이 닫혀도 Dispose 하지 않는다: 늦게 끝나는 결과 집합 정리·닫는 중인 명령이 놓을 수 있어야 한다(AvailableWaitHandle 을 쓰지 않으므로 놓아 둬도 된다).
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>지금 누가 이 연결에 명령을 보내는 중인지(시험·진단용).</summary>
    public bool IsHeld => _semaphore.CurrentCount == 0;

    /// <summary>차례가 올 때까지 기다린다. <see cref="SessionWait.Brief"/> 범위 안이면 한도를 넘길 때 <see cref="SessionBusyException"/>.</summary>
    public async Task EnterAsync(CancellationToken ct)
    {
        if (SessionWait.CurrentLimit is { } limit)
        {
            if (!await _semaphore.WaitAsync(limit, ct).ConfigureAwait(false))
            {
                throw new SessionBusyException();
            }

            return;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 동기 경로(Stream.Read 등)용. 기다림 규칙은 <see cref="EnterAsync"/> 와 같다. UI 스레드에서 부르지 않는다.
    /// </summary>
    public void Enter(CancellationToken ct)
    {
        if (SessionWait.CurrentLimit is { } limit)
        {
            if (!_semaphore.Wait(limit, ct))
            {
                throw new SessionBusyException();
            }

            return;
        }

        _semaphore.Wait(ct);
    }

    /// <summary>정리(커서 닫기 등)용: <see cref="SessionWait.Brief"/> 범위와 상관없이 차례가 올 때까지 기다린다. 취소하지 않는다.</summary>
    public Task EnterForCleanupAsync() => _semaphore.WaitAsync();

    /// <summary><see cref="EnterForCleanupAsync"/> 의 동기판(Stream.Dispose 등).</summary>
    public void EnterForCleanup() => _semaphore.Wait();

    /// <summary>바로 잡을 수 있을 때만 잡는다(Keep Alive: 바쁘면 그 자체가 활동이다).</summary>
    public bool TryEnter() => _semaphore.Wait(0);

    public void Release() => _semaphore.Release();
}
