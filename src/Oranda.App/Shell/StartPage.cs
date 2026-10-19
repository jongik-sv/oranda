using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Shell.Icons;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 문서가 하나도 없을 때 문서 영역에 보이는 시작 화면(SQL Developer 의 Start Page 자리).
/// 자주 쓰는 명령을 아이콘·단축키와 함께 늘어놓는다. 단추는 명령 레지스트리로만 실행한다.
/// </summary>
public sealed class StartPage : UserControl
{
    private static readonly string[] Commands =
    [
        "oranda.file.newSqlEditor",
        "oranda.file.newPlsqlEditor",
        "oranda.conn.manage",
        "oranda.file.open",
        "oranda.file.openWorkspaceFile",
        "oranda.view.commandPalette",
        "oranda.tools.settings",
    ];

    public StartPage(ICommandRegistry registry, IContextKeys keys, IShellText text, Func<string, string?> shortcut)
    {
        ArgumentNullException.ThrowIfNull(registry);
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

        // 창이 아주 작아도(배율 250 %) 문서 영역 밖으로 넘치지 않게 스크롤 안에 둔다(글은 말줄임 허용).
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = Body(list),
        };
    }

    private static StackPanel Body(StackPanel list) =>
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
                list,
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
}
