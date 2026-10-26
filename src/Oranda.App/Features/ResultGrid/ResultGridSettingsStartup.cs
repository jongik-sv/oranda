using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 결과 그리드가 설정 <c>grid.localSortMaxRows</c> 를 정렬할 때마다 읽도록 연결한다(TSK-13-17). 값은 읽는 순간의 현재 설정이라 설정을 바꾸면 바로 따라간다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class ResultGridSettingsStartup(SettingsService settings) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        ResultGridModel.LocalSortMaxRowsProvider = () => settings.Current.Grid.LocalSortMaxRows;
        ResultGridModel.CacheOptionsProvider = () => new ResultPageCacheOptions { MaxMemoryBytes = Math.Max(1, settings.Current.Grid.MemoryLimitMb) * 1024L * 1024 };
        Oranda.App.Shell.ShellText.TitleOverride ??= GridRowTitles.For; // 행 명령 이름에 고른 행 수(TSK-11-01)
        return Task.CompletedTask;
    }
}
