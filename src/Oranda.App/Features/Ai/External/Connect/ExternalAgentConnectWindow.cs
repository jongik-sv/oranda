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

    /// <param name="openLevels">「수준 바꾸기: 설정 &gt; AI 의 접속 표 열기」: 이 창을 닫은 뒤 부른다(설정 창 위에서 열었으면 그 탭으로 돌아갈 뿐). null 이면 링크를 숨긴다.</param>
    public ExternalAgentConnectWindow(ExternalAgentConnectViewModel vm, IAgentSkillInstaller? skills, Func<System.Threading.Tasks.Task>? openLevels = null)
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

        LevelsLink = new HyperlinkButton { Content = ExternalAgentConnectViewModel.LevelsLinkText, Padding = new Thickness(0), IsVisible = openLevels is not null };
        Avalonia.Automation.AutomationProperties.SetName(LevelsLink, ExternalAgentConnectViewModel.LevelsLinkText);
        LevelsLink.Click += async (_, _) =>
        {
            Close();
            if (openLevels is not null)
            {
                await openLevels().ConfigureAwait(true);
            }
        };
        var note = new Border
        {
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = ExternalAgentConnectViewModel.OnlyChooseNote, TextWrapping = TextWrapping.Wrap },
        };
        note.Bind(Border.BackgroundProperty, note.GetResourceObservable("OrandaTableSelectionBrush"));
        note.Bind(Border.BorderBrushProperty, note.GetResourceObservable("OrandaButtonAccentBrush"));
        var localModel = Check(ExternalAgentConnectViewModel.LocalModelText, nameof(ExternalAgentConnectViewModel.LocalModel));
        var localModelWarning = Warning(ExternalAgentConnectViewModel.LocalModelWarningText, nameof(ExternalAgentConnectViewModel.ShowsLocalModelWarning));
        localModelWarning.Margin = new Thickness(28, -6, 0, 0);
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
                    new TextBlock { Text = ExternalAgentConnectViewModel.ConnectionsTitle, FontWeight = FontWeight.SemiBold },
                    Caption(ExternalAgentConnectViewModel.ConnectionsCaption),
                    empty,
                    rows,
                    LevelsLink,
                    note,
                    localModel,
                    localModelWarning,
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

    /// <summary>「수준 바꾸기」 링크(시험용).</summary>
    internal HyperlinkButton LevelsLink { get; }

    /// <summary>마지막으로 연 스킬 설치 창(시험용).</summary>
    internal AgentSkillInstallWindow? SkillWindow { get; private set; }

    private static StackPanel RowView(ExternalAgentConnectionRow row)
    {
        var check = new CheckBox { Content = row.Alias, IsEnabled = row.Usable, MinWidth = 180 };
        check.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(ExternalAgentConnectionRow.IsChecked)) { Mode = BindingMode.TwoWay });
        var env = new TextBlock { Text = row.EnvironmentText, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60 };
        if (row.IsProductionMarked)
        {
            // 운영 행 빨간 표시(§3.2): 환경 글을 운영 색·굵게.
            env.Classes.Add(ProductionClass);
            env.FontWeight = FontWeight.SemiBold;
            env.Bind(TextBlock.ForegroundProperty, env.GetResourceObservable(DangerBrush));
            check.Classes.Add(ProductionClass);
        }

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
        var runs = Warning(ExternalAgentConnectionRow.RunWarningText, nameof(ExternalAgentConnectionRow.ShowsRunWarning));
        var unmasked = Warning(ExternalAgentConnectionRow.UnmaskedWarningText, nameof(ExternalAgentConnectionRow.ShowsUnmaskedWarning));
        runs.Margin = unmasked.Margin = new Thickness(24, 0, 0, 0);
        return new StackPanel { Spacing = 4, DataContext = row, Children = { line, runs, unmasked, confirm } };
    }

    /// <summary>운영 행·경고 글에 붙이는 클래스(시험이 빨간 표시를 찾는다).</summary>
    internal const string ProductionClass = "agent-production";

    private const string DangerBrush = "OrandaDangerForegroundBrush";

    /// <summary>빨간 경고 한 줄. <paramref name="visiblePath"/> 가 참일 때만 보인다.</summary>
    private static TextBlock Warning(string text, string visiblePath)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        block.Classes.Add(ProductionClass);
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(DangerBrush));
        block.Bind(IsVisibleProperty, new Binding(visiblePath));
        return block;
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
