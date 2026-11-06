using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사가 쓰는 원본·대상 전용 세션 한 쌍을 연고 닫는다. 원본은 <c>source with { ReadOnly = true }</c> 로 열어
/// 정책 관문이 SELECT 외 문장을 한 번 더 막게 한다(계획 Global Constraints). 둘 다 <see cref="SessionPurpose.Dedicated"/> 다.
/// </summary>
public static class DataCopySessions
{
    /// <summary>앱 연결: 비밀번호는 <see cref="DedicatedSessionCommands.OpenDedicatedAsync"/> 가 안전 저장소에서 꺼내고 없으면 묻는다.</summary>
    public static Task<CopySessions?> OpenAsync(ServiceContainer container, ConnectionProfile source, ConnectionProfile target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        return OpenAsync((profile, token) => DedicatedSessionCommands.OpenDedicatedAsync(container, profile, token), source, target, ct);
    }

    /// <summary>
    /// 세션 여는 길을 바꿀 수 있는 형태(시험은 가짜로 받은 프로필을 확인한다). 한쪽이 취소·실패하면 이미 열린 쪽을 닫고 null 을 돌려준다.
    /// 대상이 운영 접속이면 세션을 열기 전에 막는다(<see cref="CopyTargetGuard"/>).
    /// </summary>
    public static async Task<CopySessions?> OpenAsync(Func<ConnectionProfile, CancellationToken, Task<ISession?>> open,
        ConnectionProfile source, ConnectionProfile target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        CopyTargetGuard.EnsureAllowedTarget(target);

        var src = await open(source with { ReadOnly = true }, ct).ConfigureAwait(true);
        if (src is null)
        {
            return null;
        }

        ISession? dst = null;
        try
        {
            dst = await open(target, ct).ConfigureAwait(true);
        }
        finally
        {
            if (dst is null)
            {
                await ConnectionService.CloseAsync(src).ConfigureAwait(true); // 대상이 취소·실패하면 열린 원본을 남기지 않는다
            }
        }

        return dst is null ? null : new CopySessions(src, dst);
    }

    /// <summary>창을 닫을 때: 두 세션을 모두 닫는다(하나가 실패해도 다른 쪽은 닫는다).</summary>
    public static async Task CloseAsync(CopySessions? sessions)
    {
        if (sessions is null)
        {
            return;
        }

        try
        {
            await ConnectionService.CloseAsync(sessions.Source).ConfigureAwait(true);
        }
        finally
        {
            await ConnectionService.CloseAsync(sessions.Target).ConfigureAwait(true);
        }
    }
}
