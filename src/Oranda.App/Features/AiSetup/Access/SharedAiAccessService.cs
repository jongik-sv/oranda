using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;
using Oranda.Core.Events;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>
/// 「AI 가 볼 수 있는 접속」(ai-settings-one-place 2026-11-24): 접속마다 하나인 AI 허용 수준을 읽고 쓰는 한 길.
/// 설정 「AI」 탭, 도우미의 「AI 를 쓸 접속」 단계, 외부 에이전트 연결 창이 모두 이 값(<see cref="ConnectionProfile.AiAccess"/>)을 함께 쓴다 —
/// Oranda 안의 AI 는 실효 수준 그대로, 밖의 AI 에이전트는 min(실효 수준, 외부 상한)을 받는다(<see cref="AiConnectionAccess"/>).
/// 어디서 저장하든(접속 편집 포함) 버스의 <see cref="ConnectionProfileSaved"/> 를 받아 <see cref="Changed"/> 를 내므로, 다른 화면이 곧바로 따라 바뀐다.
/// 비밀번호는 건드리지 않는다(저장된 비밀번호 유지 여부만 지금대로).
/// </summary>
public sealed class SharedAiAccessService : IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> _load;
    private readonly Func<ConnectionProfile, CancellationToken, Task<ConnectionProfile>> _save;
    private readonly IDisposable? _subscription;

    /// <param name="load">접속 목록 읽기.</param>
    /// <param name="save">접속 하나 저장(저장된 접속을 돌려준다).</param>
    /// <param name="bus">접속 저장 사건을 받을 버스. null 이면 이 서비스로 저장한 것만 알린다.</param>
    public SharedAiAccessService(
        Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>> load,
        Func<ConnectionProfile, CancellationToken, Task<ConnectionProfile>> save,
        IEventBus? bus = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _subscription = bus?.Subscribe<ConnectionProfileSaved>(this, e => Changed?.Invoke(this, AiConnectionAccess.Of(e.Profile)));
        HasBus = bus is not null;
    }

    /// <summary>접속 서비스·버스로 만든다(앱 조립).</summary>
    public static SharedAiAccessService FromConnections(IConnectionService connections, IEventBus? bus)
    {
        ArgumentNullException.ThrowIfNull(connections);
        return new SharedAiAccessService(
            async ct => (await connections.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles.ToList(),
            (profile, ct) => connections.SaveProfileAsync(profile, password: null, savePassword: profile.CredentialRef is not null, ct),
            bus);
    }

    /// <summary>앱 서비스에서 만든다. 접속 서비스가 없으면 null.</summary>
    public static SharedAiAccessService? FromApp(IServiceProvider? services) =>
        services?.GetRegisteredOrDefault<IConnectionService>() is { } connections
            ? FromConnections(connections, services.GetRegisteredOrDefault<IEventBus>())
            : null;

    /// <summary>
    /// 접속 하나의 수준이 바뀌었다(어느 화면에서 저장했든). 저장한 스레드에서 난다 — 화면은 UI 스레드로 넘긴다.
    /// </summary>
    public event EventHandler<AiConnectionAccess>? Changed;

    /// <summary>버스 사건으로 다른 곳의 저장도 알린다.</summary>
    private bool HasBus { get; }

    /// <summary>저장된 접속 목록 그대로(도우미 단계·연결 창이 접속 프로필을 다룰 때).</summary>
    public Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync(CancellationToken ct) => _load(ct);

    /// <summary>
    /// 화면이 원하는 접속 꼴(<paramref name="desired"/>, 예: 도우미의 켬/끔·연결 창의 「조회 허용」)을 그 <b>실효 수준</b>으로 이 한 길에 저장하고 저장된 접속을 돌려준다.
    /// 저장 꼴은 <see cref="AiConnectionAccessRules.Apply"/>(환경 기본값이면 null). 이름 재입력이 필요하거나 고를 수 없는 수준·없는 접속이면 <see cref="InvalidOperationException"/>
    /// (부르는 화면이 저장 실패로 보인다).
    /// </summary>
    public async Task<ConnectionProfile> SaveDesiredAsync(ConnectionProfile desired, string? confirmName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var (result, profile) = await SaveLevelAsync(desired.Id, AiAccessPolicy.Effective(desired), confirmName, ct).ConfigureAwait(true);
        return result == AiAccessChange.Saved && profile is not null
            ? profile
            : throw new InvalidOperationException(result switch
            {
                AiAccessChange.NeedsNameConfirmation => "운영 접속의 AI 허용 수준을 올리려면 접속 이름을 다시 입력해야 합니다.",
                AiAccessChange.AboveMaximum => "이 접속에서 고를 수 없는 AI 허용 수준입니다.",
                _ => "접속을 찾지 못했습니다.",
            });
    }

    /// <summary>접속마다 두 쪽 값(접속 목록 순서).</summary>
    public async Task<IReadOnlyList<AiConnectionAccess>> LoadAsync(CancellationToken ct) =>
        [.. (await _load(ct).ConfigureAwait(true)).Select(AiConnectionAccess.Of)];

    /// <summary>
    /// 접속의 수준을 바꾼다. 같은 값이면 저장하지 않는다. 운영 접속의 수준을 직접 올리면 <paramref name="confirmName"/> 이 접속 이름과 같아야 한다
    /// (접속 편집과 같은 규칙). 저장하면 <see cref="Changed"/> 가 난다(버스가 있으면 버스 사건으로 한 번).
    /// </summary>
    /// <returns>결과와 (저장했거나 그대로면) 지금 값.</returns>
    public async Task<(AiAccessChange Result, AiConnectionAccess? Access)> SetLevelAsync(Guid profileId, AiAccessLevel level, string? confirmName, CancellationToken ct)
    {
        var (result, profile) = await SaveLevelAsync(profileId, level, confirmName, ct).ConfigureAwait(true);
        return (result, profile is null ? null : AiConnectionAccess.Of(profile));
    }

    private async Task<(AiAccessChange Result, ConnectionProfile? Profile)> SaveLevelAsync(Guid profileId, AiAccessLevel level, string? confirmName, CancellationToken ct)
    {
        var profile = (await _load(ct).ConfigureAwait(true)).FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return (AiAccessChange.NotFound, null);
        }

        var check = AiConnectionAccessRules.Check(profile, level, confirmName);
        if (check != AiAccessChange.Saved)
        {
            return (check, profile);
        }

        var next = AiConnectionAccessRules.Apply(profile, level);
        if (ReferenceEquals(next, profile))
        {
            return (AiAccessChange.Saved, profile);
        }

        var saved = await _save(next, ct).ConfigureAwait(true);
        if (!HasBus)
        {
            Changed?.Invoke(this, AiConnectionAccess.Of(saved)); // 버스가 있으면 접속 서비스의 저장 사건이 알린다(두 번 내지 않는다)
        }

        return (AiAccessChange.Saved, saved);
    }

    public void Dispose() => _subscription?.Dispose();
}
