using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 매퍼 파일 감시를 호스트에 붙인다(design.md 5.3). 복구(<c>SqlEditorDockWiring</c> 가 Post 로 미룸)보다 먼저 서야 되살린 작업 공간도 따른다.
/// 붙기만 하고 Language 는 건드리지 않는다(세션이 생겨야 디스크를 읽는다, D-006).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class MapperWatchStartup(MapperWatchService watch) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        watch.Attach(SqlEditorHost.Current);
        return Task.CompletedTask;
    }
}
