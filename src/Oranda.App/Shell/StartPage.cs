using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.RecentFiles;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 문서가 하나도 없을 때 문서 영역에 보이는 시작 화면(SQL Developer 의 Start Page 자리).
/// 자주 쓰는 명령을 아이콘·단축키와 함께 늘어놓는다. 단추는 명령 레지스트리로만 실행한다.
/// 옆(좁으면 아래)에 최근 파일·최근 작업 공간 두 목록을 둔다(파일 메뉴 하위 메뉴와 같은 줄, <see cref="RecentFilesService"/>).
/// </summary>
public sealed class StartPage : UserControl
{
    private static readonly string[] Commands =
    [
        "oranda.file.newSqlEditor",
        "oranda.file.newWorkspace",
        "oranda.file.newPlsqlEditor",
        "oranda.conn.manage",
        "oranda.file.open",
        "oranda.file.openWorkspaceFile",
        "oranda.view.commandPalette",
        "oranda.tools.settings",
    ];

    private readonly RecentFilesService? _recent;
    private readonly StackPanel _recentFiles = new() { Spacing = 2 };
    private readonly StackPanel _recentWorkspaces = new() { Spacing = 2 };
    private readonly string? _fileIcon;
    private readonly string? _workspaceIcon;

    public StartPage(ICommandRegistry registry, IContextKeys keys, IShellText text, Func<string, string?> shortcut, RecentFilesService? recent = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(text);
        _recent = recent;
        var list = new StackPanel { Spacing = 2, Width = 340 };
        var byId = new Dictionary<string, CommandDefinition>(StringComparer.Ordinal);
        foreach (var c in registry.Commands)
        {
            byId[c.Id] = c;
        }

        foreach (var id in Commands)
        {
            if (!byId.TryGetValue(id, out var def))
            {
                continue;
            }

            var row = new DockPanel { LastChildFill = true };
            var keyText = new TextBlock { Text = Keymaps.KeyBindingTable.ForScreen(shortcut(id)) ?? string.Empty, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            DockPanel.SetDock(keyText, Avalonia.Controls.Dock.Right);
            row.Children.Add(keyText);
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    ShellIcons.Create(def.Icon ?? "palette", 18) ?? new Control(),
                    new TextBlock { Text = text.Title(def), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            var button = new Button
            {
                Content = row,
                Command = new RegistryCommand(id, registry, keys),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Classes = { "tool", "start-item" },
            };
            Avalonia.Automation.AutomationProperties.SetName(button, text.Title(def));
            list.Children.Add(button);
        }

        _fileIcon = "fileSql"; // 「파일 열기」 의 폴더 아이콘이 아니라 파일로 읽히게
        _workspaceIcon = byId.GetValueOrDefault("oranda.file.openWorkspaceFile")?.Icon;
        var columns = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 32, LineSpacing = 18, Children = { list } };
        if (recent is not null)
        {
            columns.Children.Add(new StackPanel
            {
                Width = 340,
                Spacing = 4,
                Children =
                {
                    Heading(text.Lookup(RecentFilesService.FilesMenuKey)),
                    _recentFiles,
                    Heading(text.Lookup(RecentFilesService.WorkspacesMenuKey), top: 12),
                    _recentWorkspaces,
                },
            });
            FillRecent();
        }

        // 창이 아주 작아도(배율 250 %) 문서 영역 밖으로 넘치지 않게 스크롤 안에 둔다(글은 말줄임 허용).
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = Body(columns),
        };
    }

    /// <summary>목록이 바뀌면 다시 채운다. 시작 화면은 문서 영역 호스트마다 새로 생기므로 붙어 있는 동안만 듣는다.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_recent is not null)
        {
            _recent.Changed += OnRecentChanged;
            FillRecent();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_recent is not null)
        {
            _recent.Changed -= OnRecentChanged;
        }
    }

    private void OnRecentChanged(object? sender, EventArgs e) => FillRecent();

    private void FillRecent()
    {
        if (_recent is null)
        {
            return;
        }

        Fill(_recentFiles, _recent.MenuItems(RecentKind.File), _fileIcon);
        Fill(_recentWorkspaces, _recent.MenuItems(RecentKind.Workspace), _workspaceIcon);
    }

    /// <summary>최근 줄: 이름 + 흐린 폴더 경로(가운데 줄임), 툴팁 = 전체 경로. 비었으면 흐린 안내 한 줄.</summary>
    private static void Fill(StackPanel panel, IReadOnlyList<MenuSourceItem> lines, string? icon)
    {
        panel.Children.Clear();
        foreach (var line in lines)
        {
            if (line.Run is not { } run)
            {
                panel.Children.Add(new TextBlock { Text = line.Title, Opacity = 0.5, Margin = new Thickness(6, 2), TextTrimming = TextTrimming.CharacterEllipsis });
                continue;
            }

            var name = new TextBlock { Text = line.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 };
            var folder = new TextBlock { Text = line.Detail, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            // 가로 StackPanel 은 폭을 무한으로 주어 말줄임이 안 된다: 아이콘·이름은 왼쪽에 붙이고 경로가 남은 폭을 채운다.
            var iconControl = ShellIcons.Create(icon ?? "palette", 18) ?? new Control();
            iconControl.Margin = new Thickness(0, 0, 10, 0);
            name.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(iconControl, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(name, Avalonia.Controls.Dock.Left);
            var button = new Button
            {
                Content = new DockPanel { LastChildFill = true, Children = { iconControl, name, folder } },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Classes = { "tool", "start-item" },
            };
            ToolTip.SetTip(button, line.ToolTip);
            Avalonia.Automation.AutomationProperties.SetName(button, line.Title);
            button.Click += (_, _) => _ = RunAsync(run);
            panel.Children.Add(button);
        }
    }

    private static async Task RunAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("시작 화면 최근 항목 열기 중 예외: {0}", ex);
        }
    }

    private static TextBlock Heading(string title, double top = 0) =>
        new() { Text = title, FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(6, top, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };

    private static StackPanel Body(Panel columns) =>
        new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Oranda", FontSize = 28, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "Oracle 개발·운영 도구", Opacity = 0.6, Margin = new Thickness(0, 0, 0, 18), TextTrimming = TextTrimming.CharacterEllipsis },
                columns,
                new TextBlock
                {
                    Text = "MyBatis 매퍼(.xml)나 작업 공간(.md)을 열면 문장마다 탭이 생깁니다.",
                    Opacity = 0.5,
                    FontSize = 11,
                    Margin = new Thickness(0, 16, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, // 작은 창에서는 스크롤로 본다
                    MaxWidth = 340,
                },
            },
        };

    /// <summary>
    /// 문서 영역 모델(<see cref="Docking.DockWorkspace.EmptyDocumentContent"/>)에 넣는 표식 <see cref="StartPageContent"/> 을 화면으로 바꾸는 템플릿.
    /// 호스트(문서 영역 템플릿)마다 시작 화면을 새로 만든다 — 모델에 Control 자체를 두면 템플릿이 다시 만들어질 때 옛 호스트가 붙든 같은 Control 을
    /// 새 호스트에 붙이려다 「already has a visual parent」 예외가 난다(2026-10-24).
    /// </summary>
    public static IDataTemplate HostTemplate(Func<StartPage> create) => new StartPageTemplate(create);

    // FuncDataTemplate<T> 는 형식 검사가 null 도 맞다고 본다. 그러면 메인 창 안에서 내용이 null 인 곳(값을 안 고른 콤보의 선택 칸 등)에도
    // 시작 화면이 그려진다(2026-10-24, 시나리오 0개 문장의 시나리오 콤보). 그래서 표식만 맞게 직접 판정한다.
    private sealed class StartPageTemplate(Func<StartPage> create) : IDataTemplate
    {
        public bool Match(object? data) => data is StartPageContent;

        public Control? Build(object? param) => param is StartPageContent ? create() : null;
    }
}

/// <summary>문서가 하나도 없을 때 문서 영역에 보일 시작 화면의 표식(데이터). 화면은 <see cref="StartPage.HostTemplate"/> 이 호스트마다 만든다.</summary>
public sealed class StartPageContent
{
    public static StartPageContent Instance { get; } = new();

    private StartPageContent()
    {
    }

    /// <summary>템플릿이 없는 곳(메인 창 밖 — 떠 있는 창 등)에 놓여도 클래스 이름 글자가 보이지 않게 빈 글.</summary>
    public override string ToString() => string.Empty;
}
