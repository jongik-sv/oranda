using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// TSK-03-01 앱 셸 — 셸이 소유한 명령(commands.md 7장 「소유 Task = TSK-03-01」) 처리기.
/// 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 자동 수집된다(docs/design/ui-contract.md). 처리기는 <see cref="IShellUi"/> 만 안다.
/// 메뉴 위치(<c>Placements</c>)의 순서 값은 100 단위가 한 묶음이다(묶음 사이 구분선).
/// </summary>
public static class ShellCommands
{
    /// <summary>이 파일이 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids =
    [
        "oranda.file.close",
        "oranda.file.closeAll",
        "oranda.file.exit",
        "oranda.view.commandPalette",
        "oranda.view.resetLayout",
        "oranda.view.themeLight",
        "oranda.view.themeDark",
        "oranda.view.themeSystem",
        "oranda.view.zoomIn",
        "oranda.view.zoomOut",
        "oranda.view.zoomReset",
        "oranda.tools.settings",
        "oranda.window.nextDocument",
        "oranda.window.previousDocument",
        "oranda.window.splitRight",
        "oranda.window.splitDown",
        "oranda.window.focusDocument",
        "oranda.help.about",
    ];
}

/// <summary>파일 메뉴: 탭 닫기·모두 닫기·끝내기.</summary>
[OrandaCommand("oranda.file.close", CommandCategory.File, Enablement = "hasDocument", Placements = ["menu:file/close@800"])]
[OrandaCommand("oranda.file.closeAll", CommandCategory.File, Enablement = "hasDocument", Placements = ["menu:file/closeAll@810"])]
[OrandaCommand("oranda.file.exit", CommandCategory.File, Placements = ["menu:file/exit@900"])]
public sealed class FileShellCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        switch (context.CommandId)
        {
            case "oranda.file.close":
                if (ui.Docking.ActiveDocument is { } active)
                {
                    ui.Docking.CloseDocument(active);
                }

                break;
            case "oranda.file.closeAll":
                ui.Docking.CloseAllDocuments();
                break;
            case "oranda.file.exit":
                ui.CloseMainWindow();
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>보기 메뉴: 명령 팔레트·테마·글자 크기·레이아웃 초기화.</summary>
[OrandaCommand("oranda.view.commandPalette", CommandCategory.View, Placements = ["menu:view/commandPalette@10"])]
[OrandaCommand("oranda.view.themeLight", CommandCategory.View, Placements = ["menu:view/theme/light@110"])]
[OrandaCommand("oranda.view.themeDark", CommandCategory.View, Placements = ["menu:view/theme/dark@120"])]
[OrandaCommand("oranda.view.themeSystem", CommandCategory.View, Placements = ["menu:view/theme/system@130"])]
[OrandaCommand("oranda.view.zoomIn", CommandCategory.View, Placements = ["menu:view/zoomIn@210"])]
[OrandaCommand("oranda.view.zoomOut", CommandCategory.View, Placements = ["menu:view/zoomOut@220"])]
[OrandaCommand("oranda.view.zoomReset", CommandCategory.View, Placements = ["menu:view/zoomReset@230"])]
[OrandaCommand("oranda.view.resetLayout", CommandCategory.View, Placements = ["menu:view/resetLayout@310"])]
public sealed class ViewShellCommandHandler(IShellUi ui) : ICommandHandler
{
    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        switch (context.CommandId)
        {
            case "oranda.view.commandPalette":
                ui.ShowCommandPalette();
                break;
            case "oranda.view.themeLight":
                await ui.Settings.SetThemeAsync(Theme.Light, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.themeDark":
                await ui.Settings.SetThemeAsync(Theme.Dark, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.themeSystem":
                await ui.Settings.SetThemeAsync(Theme.System, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.zoomIn":
                await ui.Settings.ZoomAsync(Settings.SettingsService.ZoomStep, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.zoomOut":
                await ui.Settings.ZoomAsync(-Settings.SettingsService.ZoomStep, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.zoomReset":
                await ui.Settings.ZoomResetAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.view.resetLayout":
                ui.Docking.Reset();
                break;
        }
    }
}

/// <summary>도구 메뉴: 설정.</summary>
[OrandaCommand("oranda.tools.settings", CommandCategory.Tools, Placements = ["menu:tools/settings@10"])]
public sealed class SettingsShellCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ui.ShowSettings();
        return Task.CompletedTask;
    }
}

/// <summary>창 메뉴: 탭 이동·분할·문서로 포커스.</summary>
[OrandaCommand("oranda.window.nextDocument", CommandCategory.Window, Enablement = "hasDocument", Placements = ["menu:window/nextDocument@10"])]
[OrandaCommand("oranda.window.previousDocument", CommandCategory.Window, Enablement = "hasDocument", Placements = ["menu:window/previousDocument@20"])]
[OrandaCommand("oranda.window.splitRight", CommandCategory.Window, Enablement = "hasDocument", Placements = ["menu:window/splitRight@110"])]
[OrandaCommand("oranda.window.splitDown", CommandCategory.Window, Enablement = "hasDocument", Placements = ["menu:window/splitDown@120"])]
[OrandaCommand("oranda.window.focusDocument", CommandCategory.Window, Enablement = "hasDocument", Placements = ["menu:window/focusDocument@210"])]
public sealed class WindowShellCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        switch (context.CommandId)
        {
            case "oranda.window.nextDocument":
                ui.Docking.CycleDocument(1);
                break;
            case "oranda.window.previousDocument":
                ui.Docking.CycleDocument(-1);
                break;
            case "oranda.window.splitRight":
                ui.Docking.SplitActiveDocument(right: true);
                break;
            case "oranda.window.splitDown":
                ui.Docking.SplitActiveDocument(right: false);
                break;
            case "oranda.window.focusDocument":
                ui.FocusDocument();
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>도움말 메뉴: Oranda 정보.</summary>
[OrandaCommand("oranda.help.about", CommandCategory.Help, Placements = ["menu:help/about@900"])]
public sealed class AboutShellCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ui.ShowAbout();
        return Task.CompletedTask;
    }
}
