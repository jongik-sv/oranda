using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.Ai.External.Connect;

/// <summary>
/// 외부 에이전트 띠 View: 점 + 글, 오른쪽에 「끊기」. 누르면 최근 호출 목록과 「끊기」·「연장」(만료 10분 전부터)이 든 플라이아웃을 띄운다.
/// 스킬 다시 설치만 남은 상태면 누를 때 「스킬 다시 설치」 단추를 보인다. 색은 기존 리소스 키만 쓴다.
/// </summary>
public sealed class ExternalAgentStatusView : UserControl
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _dot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _disconnect = new() { Padding = new Thickness(6, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
    private ExternalAgentStatusViewModel? _vm;

    public ExternalAgentStatusView()
    {
        _disconnect.Click += (_, e) =>
        {
            e.Handled = true;
            _vm?.Disconnect();
        };
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _dot, _text, _disconnect } };
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        PointerPressed += (_, e) =>
        {
            if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                ShowDetails();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as ExternalAgentStatusViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    /// <summary>마지막으로 띄운 자세히 보기(시험용).</summary>
    internal Flyout? DetailsFlyout { get; private set; }

    /// <summary>최근 호출 목록·「끊기」·「연장」·「스킬 다시 설치」 플라이아웃.</summary>
    internal void ShowDetails()
    {
        if (_vm is not { } vm)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 6, MinWidth = 320, MaxWidth = 460 };
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, Content = panel };

        if (vm.IsConnected)
        {
            var title = new TextBlock { Text = ExternalAgentStatusViewModel.RecentCallsTitle, FontWeight = FontWeight.SemiBold };
            panel.Children.Add(title);
            if (vm.RecentCalls.Count == 0)
            {
                var none = new TextBlock { Text = ExternalAgentStatusViewModel.NoCallsText };
                none.Classes.Add("panel-caption");
                panel.Children.Add(none);
            }
            else
            {
                var rows = new StackPanel { Spacing = 2 };
                foreach (var call in vm.RecentCalls)
                {
                    rows.Children.Add(CallRow(call));
                }

                panel.Children.Add(new ScrollViewer { MaxHeight = 320, Content = rows });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            if (vm.CanExtend)
            {
                buttons.Children.Add(ActionButton(ExternalAgentStatusViewModel.ExtendText, () =>
                {
                    flyout.Hide();
                    vm.Extend();
                }));
            }

            buttons.Children.Add(ActionButton(ExternalAgentStatusViewModel.DisconnectText, () =>
            {
                flyout.Hide();
                vm.Disconnect();
            }));
            panel.Children.Add(buttons);
        }

        if (vm.SkillReinstallNeeded)
        {
            var notice = new TextBlock { Text = vm.ToolTip, TextWrapping = TextWrapping.Wrap };
            notice.Classes.Add("panel-caption");
            panel.Children.Add(notice);
            panel.Children.Add(ActionButton(ExternalAgentStatusViewModel.ReinstallText, async () =>
            {
                flyout.Hide();
                await vm.ReinstallSkillsAsync();
            }));
        }

        DetailsFlyout = flyout;
        flyout.ShowAt(this);
    }

    private static Grid CallRow(ExternalAgentCallRow call)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        var time = new TextBlock { Text = call.TimeText };
        time.Classes.Add("panel-caption");
        var tool = new TextBlock { Text = call.Tool, TextTrimming = TextTrimming.CharacterEllipsis };
        var judgement = new TextBlock { Text = call.JudgementText };
        if (call.Judgement is Oranda.Core.Ai.Contracts.AiToolJudgement.Denied or Oranda.Core.Ai.Contracts.AiToolJudgement.Failed)
        {
            judgement.FontWeight = FontWeight.SemiBold;
        }

        var size = new TextBlock { Text = call.SizeText, HorizontalAlignment = HorizontalAlignment.Right };
        size.Classes.Add("panel-caption");
        Grid.SetColumn(tool, 1);
        Grid.SetColumn(judgement, 2);
        Grid.SetColumn(size, 3);
        grid.Children.Add(time);
        grid.Children.Add(tool);
        grid.Children.Add(judgement);
        grid.Children.Add(size);
        ToolTip.SetTip(grid, call.Client);
        return grid;
    }

    private static Button ActionButton(string text, System.Action onClick)
    {
        var button = new Button { Content = text };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var text = _vm?.Text ?? string.Empty;
        _text.Text = text;
        _disconnect.Content = ExternalAgentStatusViewModel.DisconnectText;
        _disconnect.IsVisible = _vm?.IsConnected == true;
        Avalonia.Automation.AutomationProperties.SetName(_disconnect, ExternalAgentStatusViewModel.DisconnectText);
        ToolTip.SetTip(this, _vm?.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, text);
        var brush = _vm switch
        {
            { IsConnected: true, CanExtend: true } => "OrandaUncommittedBrush",
            { IsConnected: true } => "OrandaEnvDevelopmentBrush",
            _ => "OrandaButtonDisabledForegroundBrush",
        };
        _dot.Bind(Border.BackgroundProperty, _dot.GetResourceObservable(brush));
    }
}
