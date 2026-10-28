using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Bridge;
using Oranda.Core.Composition;
using Oranda.Core.Features;

namespace Oranda.App.Features.Ai.Agent;

/// <summary>
/// 시작 작업: aiTuning 기능이 켜져 있을 때만 AI 다리(<see cref="IAgentBridgeHost"/>)를 연다. 꺼져 있으면 다리를 해석하지도 열지도 않는다.
/// 외부 에이전트(<c>orandactl</c>)가 앱 시작 때부터 붙을 수 있게 하려는 것이고, 내장 튜닝은 시작할 때 한 번 더 부른다(<see cref="IAgentBridgeHost.Start"/> 는 되풀이해도 된다).
/// 이 형은 <c>Features.Ai</c> 네임스페이스라 기능 게이트가 꺼진 기능의 시작 작업을 만들지 않는 규칙도 함께 걸린다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class AgentBridgeStartup(IServiceProvider services) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.Idle;

    public Task RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Start(services);
        return Task.CompletedTask;
    }

    /// <summary>기능이 켜져 있으면 다리를 연다(게이트를 못 찾으면 열지 않는다). 열었으면 true.</summary>
    public static bool Start(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService(typeof(IFeatureGate)) is not IFeatureGate gate || !gate.IsEnabled(FeatureIds.AiTuning)) // 게이트를 못 찾으면 열지 않는다
        {
            return false;
        }

        if (services.GetService(typeof(IAgentBridgeHost)) is not IAgentBridgeHost host)
        {
            return false;
        }

        host.Start();
        return true;
    }
}
