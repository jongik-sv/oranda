using Oranda.Core.Shell;

namespace Oranda.App.Features.RecentFiles;

/// <summary>
/// 파일 메뉴 「최근 파일」·「최근 작업 공간」 하위 메뉴 맨 아래의 「목록 지우기」. 이 명령의 자리가 하위 메뉴를 만들고,
/// 그 위의 최근 항목 줄은 <see cref="RecentFilesService"/> 가 메뉴를 열 때 채운다.
/// </summary>
[OrandaCommand(ClearFiles, CommandCategory.File, Placements = ["menu:file/recentFiles/clear@110"])]
[OrandaCommand(ClearWorkspaces, CommandCategory.File, Placements = ["menu:file/workspace/recentWorkspaces/clear@302"])]
public sealed class RecentFilesCommands : ICommandHandler
{
    public const string ClearFiles = "oranda.file.clearRecentFiles";

    public const string ClearWorkspaces = "oranda.file.clearRecentWorkspaces";

    private readonly IServiceProvider? _services;

    public RecentFilesCommands()
    {
    }

    public RecentFilesCommands(IServiceProvider services) => _services = services;

    /// <summary>시험용: 다룰 목록.</summary>
    internal RecentFilesService? Service { get; init; }

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if ((Service ?? _services?.GetService(typeof(RecentFilesService)) as RecentFilesService) is { } service)
        {
            service.Clear(context.CommandId == ClearWorkspaces ? RecentKind.Workspace : RecentKind.File);
        }

        return Task.CompletedTask;
    }
}
