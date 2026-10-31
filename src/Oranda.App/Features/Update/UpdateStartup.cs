using System.Diagnostics;
using Oranda.Core.Composition;

namespace Oranda.App.Features.Update;

/// <summary>
/// 시작 확인(TSK-10-01): 첫 프레임 뒤에 서비스를 깨우고, 확인은 기다리지 않고 백그라운드로 돌린다(뒤따르는 시작 작업을 막지 않는다).
/// 시작 확인 뒤에는 앱이 떠 있는 동안 주기 확인(<see cref="UpdateService.PeriodicInterval"/>)을 이어간다.
/// 설치하지 않은 실행본이거나 설정이 꺼져 있으면 아무것도 하지 않는다. 실패는 로그만 남긴다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class UpdateStartup(UpdateService service) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await service.RunStartupAsync(UpdateService.StartupDelay, CancellationToken.None).ConfigureAwait(false);
                await service.RunPeriodicAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("시작 때 업데이트 확인 실패: {0}", ex.Message);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }
}
