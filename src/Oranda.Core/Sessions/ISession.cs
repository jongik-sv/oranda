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
