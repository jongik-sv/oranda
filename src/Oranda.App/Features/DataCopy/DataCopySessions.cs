using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DedicatedSession;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 동시 복사 레인 세션 쌍을 연 결과(<see cref="DataCopySessions.OpenWorkersAsync(DataCopyServices, ConnectionProfile, ConnectionProfile, int, CancellationToken)"/>).
/// <paramref name="Pairs"/> = 연 쌍(<see cref="DataCopySessions.CloseWorkersAsync"/> 로 닫는다), <paramref name="Note"/> = 고른 수보다 적게 열었을 때의 안내(아니면 null).
/// </summary>
public sealed record CopyWorkerSessions(IReadOnlyList<CopySessions> Pairs, string? Note);

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

    /// <summary>앱 연결: 동시 복사 레인 세션 쌍을 연다(<see cref="DataCopyServices.OpenSessionsAsync"/>, 접속마다 비밀번호 한 번).</summary>
    public static Task<CopyWorkerSessions> OpenWorkersAsync(DataCopyServices services, ConnectionProfile source, ConnectionProfile target, int count,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        return OpenWorkersAsync(services.OpenSessionsAsync, source, target, count, ct);
    }

    /// <summary>
    /// 동시 테이블 복사(PF2)의 추가 레인 세션 쌍을 <paramref name="count"/>쌍까지 연다. 원본은 <c>source with { ReadOnly = true }</c>, 대상은 그대로,
    /// 접속마다 <paramref name="openMany"/> 를 한 번 불러 비밀번호를 한 번만 묻는다. 쌍 수 = 연 원본·대상 가운데 적은 쪽이고 짝이 없는 세션은 닫는다.
    /// 고른 수보다 적게 열면 안내 글(<see cref="CopyWorkerSessions.Note"/>)을 돌려준다. 대상이 운영 접속이면 열기 전에 막는다(<see cref="CopyTargetGuard"/>).
    /// 여는 중 예외·취소가 나면 이미 연 세션을 닫고 다시 던진다.
    /// </summary>
    public static async Task<CopyWorkerSessions> OpenWorkersAsync(Func<ConnectionProfile, int, CancellationToken, Task<DedicatedSessionBatch>> openMany,
        ConnectionProfile source, ConnectionProfile target, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(openMany);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        CopyTargetGuard.EnsureAllowedTarget(target);
        if (count <= 0)
        {
            return new CopyWorkerSessions([], null);
        }

        var sources = await openMany(source with { ReadOnly = true }, count, ct).ConfigureAwait(true);
        DedicatedSessionBatch targets;
        try
        {
            // 원본을 하나도 못 열면 대상 비밀번호를 묻지 않는다. 원본을 연 만큼만 대상을 연다.
            targets = sources.Sessions.Count == 0
                ? new DedicatedSessionBatch([], null)
                : await openMany(target, sources.Sessions.Count, ct).ConfigureAwait(true);
        }
        catch
        {
            await CloseAllAsync(sources.Sessions).ConfigureAwait(true);
            throw;
        }

        var paired = Math.Min(sources.Sessions.Count, targets.Sessions.Count);
        var pairs = new List<CopySessions>(paired);
        for (var i = 0; i < paired; i++)
        {
            pairs.Add(new CopySessions(sources.Sessions[i], targets.Sessions[i]));
        }

        await CloseAllAsync([.. sources.Sessions.Skip(paired), .. targets.Sessions.Skip(paired)]).ConfigureAwait(true); // 짝이 없는 세션은 남기지 않는다
        if (paired >= count)
        {
            return new CopyWorkerSessions(pairs, null);
        }

        var reason = (sources.Sessions.Count < count ? sources.Error : targets.Error ?? sources.Error) ?? DataCopyText.L(WorkerPromptCancelled);
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var note = paired == 0
            ? Fill(DataCopyText.L(WorkersNoneOpened), count.ToString(c), reason)
            : Fill(DataCopyText.L(WorkersFewerOpened), count.ToString(c), paired.ToString(c), (paired + 1).ToString(c), reason);
        return new CopyWorkerSessions(pairs, note);
    }

    // {0}·{1}… 자리를 차례로 채운다(번역 글도 같은 자리를 쓴다). 까닭(오류 글)은 늘 마지막 자리라 그 안의 중괄호는 다시 바뀌지 않는다.
    private static string Fill(string template, params string[] values)
    {
        var text = template;
        for (var i = 0; i < values.Length; i++)
        {
            text = text.Replace("{" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", values[i], StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>레인 세션을 하나도 열지 못했을 때의 안내({0} = 고른 쌍 수, {1} = 까닭).</summary>
    public const string WorkersNoneOpened = "레인 세션을 {0}쌍 가운데 하나도 열지 못해 한 테이블씩 복사합니다: {1}";

    /// <summary>레인 세션을 고른 수보다 적게 열었을 때의 안내({0} = 고른 쌍 수, {1} = 연 쌍 수, {2} = 동시 수, {3} = 까닭).</summary>
    public const string WorkersFewerOpened = "레인 세션을 {0}쌍 가운데 {1}쌍만 열어 동시 {2}개로 줄여 복사합니다: {3}";

    /// <summary>오류 없이 적게 열린 까닭(비밀번호 입력 취소).</summary>
    public const string WorkerPromptCancelled = "비밀번호 입력을 취소했습니다.";

    /// <summary>레인 세션 쌍을 모두 닫는다(하나가 실패해도 나머지는 닫는다). 실행이 끝난 뒤(실행기가 되돌린 뒤) 부른다.</summary>
    public static async Task CloseWorkersAsync(IReadOnlyList<CopySessions>? workers)
    {
        if (workers is null)
        {
            return;
        }

        await CloseAllAsync([.. workers.SelectMany(w => new[] { w.Source, w.Target })]).ConfigureAwait(true);
    }

    private static async Task CloseAllAsync(IReadOnlyList<ISession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                await ConnectionService.CloseAsync(session).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // 한 세션을 닫지 못해도 나머지는 닫는다.
            catch (Exception e)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceWarning("레인 세션을 닫지 못했습니다: {0}", e.Message);
            }
        }
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
