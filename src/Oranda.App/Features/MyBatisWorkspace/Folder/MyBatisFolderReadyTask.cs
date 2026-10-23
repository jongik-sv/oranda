using Oranda.App.Shell;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 매퍼 폴더 기능이 들어왔음을 알린다(문맥 키 <c>mybatisFolderReady</c>, design.md 4.3·R24). 이 키가 참이어야 「매퍼 폴더 열기…」 메뉴를 누를 수 있다.
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class MyBatisFolderReadyTask(ContextKeyService keys) : IStartupTask
{
    internal const string FolderReadyKey = "mybatisFolderReady";

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        keys.Set(FolderReadyKey, true);
        return Task.CompletedTask;
    }
}
