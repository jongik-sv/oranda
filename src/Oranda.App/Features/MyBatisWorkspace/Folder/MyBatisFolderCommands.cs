using Avalonia.Platform.Storage;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 매퍼 폴더 열기·MyBatis 문장 찾기 명령의 처리기(docs/oranda1/design.md 4.1, MB-10). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결했다.
/// 폴더 열기 = 최근 폴더가 있으면 그 목록과 「다른 폴더…」 중에서 고르고, 없으면 바로 폴더 고르기 창.
/// 문장 찾기 = namespace.id 또는 id 일부를 받아 등록된 찾기(열린 매퍼·매퍼 폴더)를 모두 묻고, 하나면 바로 열고 여럿이면 고르게 한다.
/// </summary>
[OrandaCommand("oranda.mybatis.openFolder", CommandCategory.File, Icon = "folder", Enablement = "mybatisFolderReady", Placements = ["menu:file/mybatis/mybatisOpenFolder@330"])]
[OrandaCommand("oranda.mybatis.findStatement", CommandCategory.Navigate, Icon = "find", Enablement = "mybatisDocActive || mybatisFolderOpen", Placements = ["menu:edit/go/mybatisFind@520"])]
public sealed class MyBatisFolderCommands : ICommandHandler
{
    internal const string OtherFolderItem = "다른 폴더…";

    private readonly IServiceProvider? _services;
    private SqlEditorHost? _host;
    private MapperFolderService? _folder;

    // 인자 없는 생성자는 시험·헤드리스용이다. 셸은 IServiceProvider 를 받는 생성자를 고른다(MyBatisStatementCommands 와 같은 모양).
    public MyBatisFolderCommands()
    {
    }

    public MyBatisFolderCommands(IServiceProvider services) => _services = services;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>(시험은 초기화 구문으로 준다).</summary>
    internal SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    /// <summary>매퍼 폴더 상태. 기본은 셸에 등록된 것(시험은 초기화 구문으로 준다).</summary>
    internal MapperFolderService? Folder
    {
        get => _folder ??= _services?.GetService(typeof(MapperFolderService)) as MapperFolderService;
        init => _folder = value;
    }

    /// <summary>폴더 고르기 창. 기본은 주 창의 저장소 공급자(시험은 바꾼다). 취소하면 null.</summary>
    internal Func<CancellationToken, Task<string?>>? PickFolder { get; init; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case "oranda.mybatis.openFolder":
                await OpenFolderAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.mybatis.findStatement":
                await FindStatementAsync(cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    private async Task OpenFolderAsync(CancellationToken ct)
    {
        const string title = "매퍼 폴더 열기";
        if (Folder is not { } folder)
        {
            return;
        }

        var host = Host;
        string? picked = null;
        if (folder.RecentFolders.Count > 0)
        {
            List<string> items = [.. folder.RecentFolders, OtherFolderItem];
            var choice = await host.Dialogs.ChooseAsync(title, items, ct).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            int at = items.IndexOf(choice);
            picked = at >= 0 && at < items.Count - 1 ? items[at] : null; // 마지막 줄 = 「다른 폴더…」
        }

        picked ??= await (PickFolder ?? PickWithMainWindowAsync)(ct).ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }

        if (!Directory.Exists(picked))
        {
            folder.Forget(picked); // 없어진 최근 폴더는 목록에서 뺀다
            await host.Dialogs.ShowMessageAsync(title, $"폴더를 찾을 수 없습니다: {picked}", ct).ConfigureAwait(true);
            return;
        }

        await folder.OpenAsync(picked, ct).ConfigureAwait(true);
    }

    private async Task FindStatementAsync(CancellationToken ct)
    {
        const string title = "MyBatis 쿼리 찾기";
        var host = Host;
        var query = await host.Dialogs.PromptAsync(title, string.Empty, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var found = await FindAsync(host, query.Trim(), exact: false, ct).ConfigureAwait(true);
        if (found.Count == 0)
        {
            await host.Dialogs.ShowMessageAsync(title, $"'{query.Trim()}' 문장을 찾지 못했습니다.", ct).ConfigureAwait(true);
            return;
        }

        var target = found[0];
        if (found.Count > 1)
        {
            var labels = found.Select(Label).ToList();
            var choice = await host.Dialogs.ChooseAsync(title, labels, ct).ConfigureAwait(true);
            if (choice is null)
            {
                return;
            }

            target = found[labels.IndexOf(choice)];
        }

        await MapperFolderOpener.OpenStatementAsync(host, target.MapperPath, target.StatementId, ct).ConfigureAwait(true);
    }

    /// <summary>등록된 찾기를 모두 묻고 결과를 합친다(같은 매퍼·id 는 하나로, 먼저 온 것을 남긴다).</summary>
    internal static async Task<IReadOnlyList<MyBatisStatementLocation>> FindAsync(SqlEditorHost host, string query, bool exact, CancellationToken ct)
    {
        var all = new List<MyBatisStatementLocation>();
        var seen = new HashSet<(string, string)>();
        foreach (var locator in host.MyBatisHooks.StatementLocators)
        {
            foreach (var hit in await locator.FindAsync(query, exact, ct).ConfigureAwait(true))
            {
                if (seen.Add((Path.GetFullPath(hit.MapperPath).ToUpperInvariant(), hit.StatementId)))
                {
                    all.Add(hit);
                }
            }
        }

        return all;
    }

    /// <summary>고르기 목록 한 줄: <c>namespace.id — 매퍼 경로</c>(경로가 있어 줄마다 다르다).</summary>
    internal static string Label(MyBatisStatementLocation hit)
    {
        var name = hit.MapperNamespace is null ? hit.StatementId : hit.MapperNamespace + "." + hit.StatementId;
        return $"{name} — {hit.MapperPath}";
    }

    private async Task<string?> PickWithMainWindowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if ((_services?.GetService(typeof(ShellController)) as ShellController)?.MainWindow?.StorageProvider is not { } sp)
        {
            return null;
        }

        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "매퍼 폴더 열기", AllowMultiple = false }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
