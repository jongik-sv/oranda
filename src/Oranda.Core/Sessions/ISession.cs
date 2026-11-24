using Oranda.Core.Connection;

namespace Oranda.Core.Sessions;

/// <summary>
/// 물리 연결 1개(TRD 4.1). 실행은 이 객체가 아니라 <c>IExecutionService</c> 만 통한다(TRD 4.9).
/// 닫으면 <c>SessionClosed</c>, 끊김을 감지하면 <c>SessionBroken</c> 이벤트를 낸다.
/// </summary>
public interface ISession : IAsyncDisposable
{
    Guid Id { get; }

    ConnectionProfile Profile { get; }

    ServerInfo Server { get; }

    SessionPurpose Purpose { get; }

    /// <summary>메타데이터 캐시 키 <c>{DB_UNIQUE_NAME}/{CON_NAME}/{SESSION_USER}</c> 대문자(data-model 3.3).</summary>
    string DbKey { get; }

    SessionState State { get; }

    bool HasPendingTransaction { get; }

    /// <summary>
    /// 지금 이 연결에 명령이 가는 중인지: 실행·컴파일·메타데이터 읽기·커밋 등 누가 세션 명령 잠금(<c>SessionCommandGate</c>)을 쥐고 있거나 실행 중(<see cref="SessionState.Busy"/>).
    /// 종료·접속 끊기가 이것을 보고 「실행 중인 작업을 취소하고 닫을까요?」를 묻는다(잠금 뒤에 커밋·롤백을 줄 세워 멈추지 않게). 기본은 <see cref="SessionState.Busy"/> 만 본다.
    /// </summary>
    bool IsCommandRunning => State == SessionState.Busy;

    Task CommitAsync(CancellationToken ct);

    Task RollbackAsync(CancellationToken ct);
}

/// <summary>
/// 편집 도구가 공유하는 작업 세션 / 모니터링 도구 전용 세션(CONN-07) / SQL 탭 하나만 쓰는 전용 세션(D-017, SQL Developer 의 unshared worksheet).
/// 전용 세션은 주 작업 세션을 바꾸지 않는다(작업 세션을 고르는 곳은 <see cref="Work"/> 만 본다).
/// </summary>
public enum SessionPurpose
{
    Work,
    Monitor,
    Dedicated,
}

public enum SessionState
{
    Idle,
    Busy,
    Broken,
    Closed,
}
