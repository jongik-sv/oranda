using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Language.Snippets;

namespace Oranda.App.Features.Templates;

/// <summary>
/// TSK-04-07 코드 조각·템플릿·키맵 프리셋·단축키 사용자 정의 — 명령 처리기 모음.
/// 명령 ID 는 commands.md 7장 「소유 Task」 열을 따른다.
/// </summary>
public static class TemplatesCommands
{
    /// <summary>이 Task 의 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.edit.insertSnippet",
        "oranda.view.templates",
        "oranda.tools.keymap",
        "oranda.tools.keymapPreset.default",
        "oranda.tools.keymapPreset.orange5",
        "oranda.tools.keymapPreset.plsqldev",
        "oranda.tools.keymapPreset.sqldev",
        "oranda.tools.exportSettings",
        "oranda.tools.importSettings",
    ];

    private const string PresetPrefix = "oranda.tools.keymapPreset.";

    internal static string? PresetOf(string commandId) =>
        commandId.StartsWith(PresetPrefix, StringComparison.Ordinal) ? commandId[PresetPrefix.Length..] : null;
}

/// <summary>
/// 코드 조각 삽입, 템플릿 창, 키맵 프리셋 전환, 단축키 설정 창, 설정 내보내기·가져오기.
/// 셸의 처리기 팩터리(DI)가 <see cref="IServiceProvider"/> 를 넣어 준다. 인자 없는 생성자는 편집기 쪽 명령만 처리한다.
/// 내보내기·가져오기는 <see cref="CommandContext.Parameter"/> 가 문자열 경로면 그 파일을 쓴다(없으면 파일 선택).
/// </summary>
[OrandaCommand("oranda.edit.insertSnippet", CommandCategory.Edit, Title = "코드 조각 삽입", When = "editorFocus", Enablement = "!readOnlyDoc", Placements = ["menu:edit/assist/insertSnippet@242"])]
[OrandaCommand("oranda.view.templates", CommandCategory.View, Title = "템플릿")]
[OrandaCommand("oranda.tools.keymap", CommandCategory.Tools, Title = "단축키 설정", Placements = ["menu:tools/keymap@30"])]
[OrandaCommand("oranda.tools.keymapPreset.default", CommandCategory.Tools, Title = "단축키 프리셋: 기본", Placements = ["menu:tools/keymapPreset/default@40"])]
[OrandaCommand("oranda.tools.keymapPreset.orange5", CommandCategory.Tools, Title = "단축키 프리셋: Orange 5", Placements = ["menu:tools/keymapPreset/orange5@41"])]
[OrandaCommand("oranda.tools.keymapPreset.plsqldev", CommandCategory.Tools, Title = "단축키 프리셋: PL/SQL Developer", Placements = ["menu:tools/keymapPreset/plsqldev@42"])]
[OrandaCommand("oranda.tools.keymapPreset.sqldev", CommandCategory.Tools, Title = "단축키 프리셋: SQL Developer", Placements = ["menu:tools/keymapPreset/sqldev@43"])]
[OrandaCommand("oranda.tools.exportSettings", CommandCategory.Tools, Title = "설정 내보내기", Placements = ["menu:tools/exportSettings@50"])]
[OrandaCommand("oranda.tools.importSettings", CommandCategory.Tools, Title = "설정 가져오기", Placements = ["menu:tools/importSettings@51"])]
public sealed class TemplatesCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;
    private SqlEditorHost? _host;

    public TemplatesCommandHandler()
    {
    }

    public TemplatesCommandHandler(IServiceProvider services) => _services = services;

    public SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case "oranda.edit.insertSnippet":
                await InsertSnippetAsync(context.Parameter, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.templates":
                (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ShowTool("oranda.panel.templates");
                break;
            case "oranda.tools.keymap":
                await ShowKeymapEditorAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.tools.exportSettings":
                await ExportAsync(context.Parameter as string, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.tools.importSettings":
                await ImportAsync(context.Parameter as string, cancellationToken).ConfigureAwait(true);
                break;
            default:
                if (TemplatesCommands.PresetOf(context.CommandId) is { } preset && _services?.GetService(typeof(SettingsService)) is SettingsService settings)
                {
                    await settings.UpdateAsync(s => s with { KeymapId = preset }, cancellationToken).ConfigureAwait(true);
                }

                break;
        }
    }

    private async Task InsertSnippetAsync(object? parameter, CancellationToken ct)
    {
        if (Host.Active is not { } tab)
        {
            return;
        }

        var user = await LoadUserSnippetsAsync(ct).ConfigureAwait(true);
        switch (parameter)
        {
            case Snippet snippet:
                SnippetInserter.Insert(tab, snippet);
                return;
            case string prefix when SnippetLibrary.FindByPrefix(prefix, user) is { } found:
                SnippetInserter.Insert(tab, found);
                return;
        }

        // 인자가 없으면 캐럿 앞 낱말을 줄임말로 펼치고, 아니면 목록에서 고른다.
        if (SnippetInserter.TryExpandPrefix(tab, user))
        {
            return;
        }

        var choices = SnippetLibrary.Search(null, SnippetKind.Snippet, user);
        var picked = await Host.Dialogs.ChooseAsync("코드 조각", [.. choices.Select(s => $"{s.Prefix}  {s.Name}")], ct).ConfigureAwait(true);
        if (picked is not null && choices.FirstOrDefault(s => picked.StartsWith(s.Prefix + "  ", StringComparison.Ordinal)) is { } chosen)
        {
            SnippetInserter.Insert(tab, chosen);
        }
    }

    private async Task<IReadOnlyList<Snippet>> LoadUserSnippetsAsync(CancellationToken ct)
    {
        if (_services?.GetService(typeof(IAppDataPaths)) is not IAppDataPaths paths)
        {
            return [];
        }

        try
        {
            return await new SnippetStore(paths).LoadAsync(ct).ConfigureAwait(true);
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private async Task ShowKeymapEditorAsync(CancellationToken ct)
    {
        if (_services?.GetService(typeof(IAppDataPaths)) is not IAppDataPaths paths
            || _services.GetService(typeof(SettingsService)) is not SettingsService settings
            || _services.GetService(typeof(ICommandRegistry)) is not ICommandRegistry registry)
        {
            return;
        }

        var store = new UserKeymapStore(paths);
        var existing = KeymapLoaderIsPreset(settings.Current.KeymapId) ? null : await store.LoadAsync(settings.Current.KeymapId, ct).ConfigureAwait(true);
        var window = new KeymapEditorWindow { DataContext = new KeymapEditorViewModel(registry.Commands, store, settings, existing, OperatingSystem.IsMacOS()) };
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }
    }

    private static bool KeymapLoaderIsPreset(string id) => Shell.Keymaps.KeymapLoader.PresetIds.Contains(id);

    private async Task ExportAsync(string? path, CancellationToken ct)
    {
        if (_services?.GetService(typeof(IAppDataPaths)) is not IAppDataPaths paths || _services.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return;
        }

        path ??= await Host.Dialogs.PickSaveFileAsync("oranda-settings.json", ct).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var transfer = new SettingsTransfer(paths, new UserKeymapStore(paths));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, transfer.Export(settings.Current), ct).ConfigureAwait(true);
    }

    private async Task ImportAsync(string? path, CancellationToken ct)
    {
        if (_services?.GetService(typeof(IAppDataPaths)) is not IAppDataPaths paths || _services.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return;
        }

        path ??= await Host.Dialogs.PickOpenFileAsync(ct).ConfigureAwait(true);
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var transfer = new SettingsTransfer(paths, new UserKeymapStore(paths));
        var imported = await transfer.ImportAsync(await File.ReadAllTextAsync(path, ct).ConfigureAwait(true), ct).ConfigureAwait(true);
        await settings.UpdateAsync(_ => imported, ct).ConfigureAwait(true);
    }
}
