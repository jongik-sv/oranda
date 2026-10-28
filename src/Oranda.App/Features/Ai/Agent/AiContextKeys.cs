using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.Agent;

/// <summary>
/// 시작 작업: AI 튜닝 명령의 문맥 키 둘을 설정과 보이는 세션에 맞춰 둔다.
/// <c>aiConfigured</c> = 설정의 AI 모델이 「선택 안 함」 이 아니다, <c>aiAccessOn</c> = 보이는 세션 접속의 AI 허용 수준이 「끔」 이 아니다.
/// 명령 처리기는 처음 실행할 때 만들어지므로 키는 처리기가 아니라 이 작업이 유지한다. <c>Features.Ai</c> 네임스페이스라 aiTuning 이 꺼져 있으면 만들어지지 않는다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class AiContextKeys(IServiceProvider services) : IStartupTask
{
    public const string ConfiguredKey = "aiConfigured";

    public const string AccessOnKey = "aiAccessOn";

    private IDisposable? _settingsSubscription;

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attach();
        return Task.CompletedTask;
    }

    /// <summary>키를 한 번 맞추고, 설정(<c>ai</c> 섹션)이나 보이는 세션이 바뀔 때마다 다시 맞춘다.</summary>
    public void Attach()
    {
        if (services.GetService(typeof(IEventBus)) is not IEventBus bus)
        {
            return;
        }

        var state = ExecutionState.For(bus);
        _settingsSubscription = bus.Subscribe<SettingsChanged>(this, e =>
        {
            if (e.Section == "ai")
            {
                Post(Sync);
            }
        });
        state.PropertyChanged += (_, _) => Post(Sync);
        Sync();
    }

    /// <summary>키 값을 지금 설정·세션에 맞춰 쓴다.</summary>
    public void Sync()
    {
        if (services.GetService(typeof(IContextKeys)) is not ContextKeyService keys)
        {
            return;
        }

        var configured = services.GetService(typeof(SettingsService)) is SettingsService settings && settings.Current.Ai.Driver != AiDriverKind.None;
        var accessOn = services.GetService(typeof(IEventBus)) is IEventBus bus
            && ExecutionState.For(bus).ShownProfile is { } profile
            && AiAccessPolicy.Effective(profile) != AiAccessLevel.Off;
        keys.Set(ConfiguredKey, configured);
        keys.Set(AccessOnKey, accessOn);
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
