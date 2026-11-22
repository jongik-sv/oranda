using System;
using System.Linq;
using Oranda.App.Shell.Composition;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// <see cref="IExternalAgentLink"/> 의 실제 구현: 허가 저장소(<see cref="IAgentGrants"/>, A2)와 외부 관문의 호출 완료 사건
/// (<see cref="IAgentExternalGate.ExternalToolCompleted"/>, A3)을 띠 모양으로 옮긴다.
/// <para>
/// 만들 때는 아무것도 풀지 않는다. 상태 표시줄 칸이 앱 시작 때 이 형을 만들기 때문이다 — 허가 저장소·게이트는 생성자 인자
/// (<c>TimeProvider</c> 등)를 풀면서 지연 어셈블리를 깨울 수 있다. 연결 창이 허가를 만들 때 <see cref="Bind"/> 를 부르면 그때 잇는다.
/// </para>
/// </summary>
[OrandaService(typeof(IExternalAgentLink))]
public sealed class ExternalAgentLink(IServiceProvider services) : IExternalAgentLink, IDisposable
{
    private readonly object _lock = new();
    private IAgentGrants? _grants;
    private IAgentExternalGate? _gate;

    public event EventHandler? Changed;

    public event EventHandler<ExternalAgentCall>? CallCompleted;

    public ExternalAgentLinkState? Current =>
        Grant() is { } grant ? new ExternalAgentLinkState(grant.Clients, grant.ExpiresAt) : null;

    /// <summary>허가 저장소와 관문에 잇는다(한 번만). 연결 창이 허가를 만들기 전에 부른다. 저장소가 없으면 아무 일도 하지 않는다.</summary>
    public void Bind()
    {
        lock (_lock)
        {
            if (_grants is not null)
            {
                return;
            }

            _grants = services.GetRegisteredOrDefault<IAgentGrants>();
            if (_grants is null)
            {
                return;
            }

            _grants.Changed += OnGrantChanged;
            _gate = services.GetRegisteredOrDefault<IAgentExternalGate>();
            if (_gate is not null)
            {
                _gate.ExternalToolCompleted += OnToolCompleted;
            }
        }
    }

    public void Disconnect()
    {
        if (Grant() is { } grant)
        {
            _grants!.Revoke(grant.GrantId);
        }
    }

    public void Extend()
    {
        if (Grant() is { } grant)
        {
            _grants!.Extend(grant.GrantId, AgentGrantPolicy.DefaultLifetime);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_grants is not null)
            {
                _grants.Changed -= OnGrantChanged;
            }

            if (_gate is not null)
            {
                _gate.ExternalToolCompleted -= OnToolCompleted;
            }
        }
    }

    private AgentGrantView? Grant() => _grants?.Active is { Count: > 0 } active ? active[0] : null;

    private void OnGrantChanged(object? sender, AgentGrantChanged e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnToolCompleted(object? sender, AgentExternalToolActivity e)
    {
        // 지난 허가(끊은 뒤 늦게 끝난 호출)는 띠에 넣지 않는다.
        if (Grant() is { } grant && string.Equals(grant.GrantId, e.GrantId, StringComparison.Ordinal))
        {
            CallCompleted?.Invoke(this, new ExternalAgentCall(e.Client, e.ToolName, e.Judgement, e.Size));
        }
    }
}
