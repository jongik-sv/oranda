using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.Platform.Ai.Skills;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 「외부 에이전트 연결」 창(한 화면, 단추 셋: 「연결」·「스킬 설치…」·「닫기」). 코드로 만든다(뷰모델 바인딩).
/// 연결된 동안은 위쪽에 상태와 「끊기」·「연장」이 보인다.
/// </summary>
public sealed class ExternalAgentConnectWindow : Window
{
    private readonly ExternalAgentConnectViewModel _vm;
    private readonly IAgentSkillInstaller? _skills;

    public ExternalAgentConnectWindow(ExternalAgentConnectViewModel vm, IAgentSkillInstaller? skills)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _skills = skills;
        DataContext = vm;
        Title = ExternalAgentText.T("외부 에이전트 연결", "External agent connection");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var intro = Caption(ExternalAgentText.T(
            "밖의 LLM CLI(Claude Code·Codex·Antigravity·opencode)가 orandactl 로 이 창을 다룹니다. 탭 글 읽기·제안·새 탭·SELECT 조회만 하고, 바꾸는 문장은 사람이 실행합니다.",
            "Outside LLM CLIs (Claude Code, Codex, Antigravity, opencode) work with this window through orandactl. They read tabs, propose edits, open new tabs and run SELECT only; you run changing statements yourself."));

        var status = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(ExternalAgentConnectViewModel.StatusText)));
        var disconnect = Action(ExternalAgentText.T("끊기", "Disconnect"), () => _vm.Disconnect());
        disconnect.Bind(IsVisibleProperty, new Binding(nameof(ExternalAgentConnectViewModel.IsConnected)));
        var extend = Action(ExternalAgentText.T("연장", "Extend"), () => _vm.Extend());
        extend.Bind(IsVisibleProperty, new Binding(nameof(ExternalAgentConnectViewModel.CanExtend)));
        var statusRow = new DockPanel { LastChildFill = true };
        var statusButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { extend, disconnect } };
        DockPanel.SetDock(statusButtons, Avalonia.Controls.Dock.Right);
        statusRow.Children.Add(statusButtons);
        statusRow.Children.Add(status);

        var rows = new ItemsControl { ItemTemplate = new FuncDataTemplate<ExternalAgentConnectionRow>((row, _) => RowView(row)) };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ExternalAgentConnectViewModel.Rows)));
        var empty = Caption(ExternalAgentText.T("저장된 접속이 없습니다. 접속 없이 연결하면 편집기 도구만 씁니다.", "No saved connections. Without one, only editor tools work."));
        empty.IsVisible = vm.Rows.Count == 0;

        var queryRows = Check(ExternalAgentText.T("조회 허용 — 고른 개발·시험 접속의 AI 허용 수준을 「조회 + 행 값」으로 올려 저장", "Allow queries — raise the chosen development/test connections to \"Query + rows\" and save it"),
            nameof(ExternalAgentConnectViewModel.AllowQueryRows));
        var expire = Check(ExternalAgentText.T("8시간 뒤 끊기", "Disconnect after 8 hours"), nameof(ExternalAgentConnectViewModel.ExpireIn8Hours));
        var unbound = Check(ExternalAgentText.T("접속 없는 탭의 글 읽기 허용", "Allow reading tabs without a connection"), nameof(ExternalAgentConnectViewModel.AllowUnboundTabText));

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(ExternalAgentConnectViewModel.Error)));
        error.Bind(IsVisibleProperty, new Binding(nameof(ExternalAgentConnectViewModel.Error)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });

        var connect = new Button { Content = ExternalAgentText.T("연결", "Connect"), IsDefault = true, Classes = { "accent" } };
        Avalonia.Automation.AutomationProperties.SetName(connect, ExternalAgentText.T("연결", "Connect"));
        connect.Bind(IsEnabledProperty, new Binding(nameof(ExternalAgentConnectViewModel.CanConnect)));
        connect.Click += async (_, _) => await _vm.ConnectAsync();
        var install = Action(ExternalAgentText.T("스킬 설치…", "Install skill…"), ShowSkills);
        install.IsEnabled = _skills is not null;
        var close = new Button { Content = ExternalAgentText.T("닫기", "Close"), IsCancel = true };
        close.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    intro,
                    statusRow,
                    new TextBlock { Text = ExternalAgentText.T("쓸 접속", "Connections to use"), FontWeight = FontWeight.SemiBold },
                    Caption(ExternalAgentText.T("개발은 기본으로 고릅니다. 운영은 메타데이터만 쓰고, 고르려면 접속 이름을 다시 입력합니다.",
                        "Development is chosen by default. Production gives metadata only and needs its name typed again.")),
                    empty,
                    rows,
                    queryRows,
                    expire,
                    unbound,
                    error,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { install, close, connect },
                    },
                },
            },
        };
    }

    /// <summary>마지막으로 연 스킬 설치 창(시험용).</summary>
    internal AgentSkillInstallWindow? SkillWindow { get; private set; }

    private static StackPanel RowView(ExternalAgentConnectionRow row)
    {
        var check = new CheckBox { Content = row.Alias, IsEnabled = row.Usable, MinWidth = 180 };
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(ExternalAgentConnectionRow.IsChecked)) { Mode = BindingMode.TwoWay });
        var env = new TextBlock { Text = row.EnvironmentText, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60 };
        var level = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (row.Usable)
        {
            level.Bind(TextBlock.TextProperty, new Binding(nameof(ExternalAgentConnectionRow.LevelText)));
        }
        else
        {
            level.Text = ExternalAgentText.T("AI 허용 수준 끔", "AI access off");
        }

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { check, env, level } };
        var confirm = new TextBox { PlaceholderText = row.ConfirmPlaceholder, Margin = new Thickness(24, 0, 0, 0), MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        confirm.Bind(TextBox.TextProperty, new Binding(nameof(ExternalAgentConnectionRow.ConfirmName)) { Mode = BindingMode.TwoWay });
        confirm.Bind(IsVisibleProperty, new Binding(nameof(ExternalAgentConnectionRow.NeedsConfirmation)));
        Avalonia.Automation.AutomationProperties.SetName(confirm, row.ConfirmPlaceholder);
        return new StackPanel { Spacing = 4, DataContext = row, Children = { line, confirm } };
    }

    private static CheckBox Check(string text, string path)
    {
        var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
        box.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        Avalonia.Automation.AutomationProperties.SetName(box, text);
        return box;
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("panel-caption");
        return block;
    }

    private static Button Action(string text, Action onClick)
    {
        var button = new Button { Content = text };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ShowSkills()
    {
        if (_skills is null)
        {
            return;
        }

        SkillWindow = new AgentSkillInstallWindow(new AgentSkillInstallViewModel(_skills));
        _ = SkillWindow.ShowDialog(this);
    }
}
