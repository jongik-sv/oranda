using Oranda.App.Shell.Settings;
using Oranda.Core.Composition;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 결과 그리드가 설정 <c>grid.localSortMaxRows</c> 를 정렬할 때마다 읽도록 연결한다(TSK-13-17). 값은 읽는 순간의 현재 설정이라 설정을 바꾸면 바로 따라간다.
/// 변경 이력(VERSIONS) 결과의 바뀐 칸 표시 훅도 여기서 건다(<see cref="Oranda.App.Features.Flashback.VersionsHighlight"/>).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class ResultGridSettingsStartup(SettingsService settings, IServiceProvider? services) : IStartupTask
{
    /// <summary>설정만 잇는 시험용(변경 이력 칸 표시 훅은 걸지 않는다).</summary>
    public ResultGridSettingsStartup(SettingsService settings)
        : this(settings, null)
    {
    }

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        ResultGridModel.LocalSortMaxRowsProvider = () => settings.Current.Grid.LocalSortMaxRows;
        ResultGridModel.CacheOptionsProvider = () => new ResultPageCacheOptions { MaxMemoryBytes = Math.Max(1, settings.Current.Grid.MemoryLimitMb) * 1024L * 1024 };
        SqlResultPaneViewModel.ColumnFilterDefaultProvider = () => settings.Current.Grid.ColumnFilterEnabled;
        SqlResultPaneViewModel.ColumnFilterDefaultSaver = on => settings.UpdateAsync(s => s with { Grid = s.Grid with { ColumnFilterEnabled = on } });
        Oranda.App.Shell.ShellText.TitleOverride ??= GridRowTitles.For; // 행 명령 이름에 고른 행 수(TSK-11-01)
        if (services is not null)
        {
            Oranda.App.Features.Flashback.VersionsHighlight.Install(services); // 변경 이력(VERSIONS) 결과의 앞 버전과 다른 칸 바탕
        }

        return Task.CompletedTask;
    }
}
