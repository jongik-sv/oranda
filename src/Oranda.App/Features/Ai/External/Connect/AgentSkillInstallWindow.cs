using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 「스킬 설치」 창: 대상 폴더마다 체크 칸·읽는 CLI·경로·상태, 남의 파일이면 「덮어쓰기」. 아래에 orandactl 경로와 결과 글, 단추 「설치」·「지우기」·「닫기」.
/// </summary>
public sealed class AgentSkillInstallWindow : Window
{
    public AgentSkillInstallWindow(AgentSkillInstallViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;
        Title = ExternalAgentText.T("에이전트 스킬 설치", "Install agent skill");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var intro = new TextBlock
        {
            Text = ExternalAgentText.T(
                "LLM CLI 가 orandactl 쓰는 법을 읽는 oranda 스킬(SKILL.md)을 사용자 폴더에 씁니다. Oranda 가 쓴 파일만 다시 쓰거나 지웁니다.",
                "Writes the oranda skill (SKILL.md) that tells LLM CLIs how to use orandactl into your user folders. Only files written by Oranda are rewritten or removed."),
            TextWrapping = TextWrapping.Wrap,
        };
        intro.Classes.Add("panel-caption");

        var rows = new ItemsControl { ItemTemplate = new FuncDataTemplate<AgentSkillRow>((row, _) => RowView(vm, row)) };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AgentSkillInstallViewModel.Rows)));

        var path = new SelectableTextBlock { Text = ExternalAgentText.T("orandactl 경로: ", "orandactl path: ") + vm.OrandactlPath, TextWrapping = TextWrapping.Wrap };
        path.Classes.Add("panel-caption");
        var message = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        message.Bind(TextBlock.TextProperty, new Binding(nameof(AgentSkillInstallViewModel.Message)));

        var install = Button(ExternalAgentText.T("설치", "Install"), async () => await vm.InstallAsync());
        install.Classes.Add("accent");
        var remove = Button(ExternalAgentText.T("지우기", "Remove"), async () => await vm.UninstallAsync());
        var close = new Button { Content = ExternalAgentText.T("닫기", "Close"), IsCancel = true };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                intro,
                rows,
                path,
                message,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { remove, close, install } },
            },
        };
    }

    private static StackPanel RowView(AgentSkillInstallViewModel vm, AgentSkillRow row)
    {
        var check = new CheckBox { Content = row.Clients, MinWidth = 200 };
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(AgentSkillRow.IsChecked)) { Mode = BindingMode.TwoWay });
        Avalonia.Automation.AutomationProperties.SetName(check, row.Clients);
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        state.Bind(TextBlock.TextProperty, new Binding(nameof(AgentSkillRow.StateText)));
        var overwrite = Button(ExternalAgentText.T("덮어쓰기", "Overwrite"), async () => await vm.OverwriteAsync(row));
        overwrite.Bind(IsVisibleProperty, new Binding(nameof(AgentSkillRow.SkippedForeign)));
        var file = new TextBlock { Text = row.FilePath, Margin = new Thickness(28, 0, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        file.Classes.Add("panel-caption");
        return new StackPanel
        {
            Spacing = 2,
            DataContext = row,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { check, state, overwrite } },
                file,
            },
        };
    }

    private static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }
}
