namespace Oranda.Core.Composition;

/// <summary>
/// 앱 시작 뒤 한 번 도는 백그라운드 작업(이력·캐시 보존 정책, ANTLR 워밍업, 이벤트 구독 시작 등).
/// <c>[OrandaService(typeof(IStartupTask))]</c> 로 자동 수집되고 앱이 <see cref="Phase"/> 시점에 부른다.
/// 시작 경로(첫 프레임 전)에서는 돌지 않는다(D-006).
/// </summary>
public interface IStartupTask
{
    StartupPhase Phase { get; }

    Task RunAsync(CancellationToken ct);
}

public enum StartupPhase
{
    /// <summary>첫 프레임을 그린 직후.</summary>
    AfterFirstFrame,

    /// <summary>입력이 잠시 멈춘 유휴 시간.</summary>
    Idle,
}
