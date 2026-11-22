using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.AiSetup.Contract;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>
/// 「AI 연결 도우미」 창(대화상자). 왼쪽 = 지금 갈래의 단계 목록, 오른쪽 = 지금 단계 화면, 아래 = [닫기]·[이전]·[다음](마지막 단계 [끝내기]).
/// 코드로 만든다(뷰모델 바인딩). 닫으면(어떤 길이든) 지금 단계를 떠나고 이어 하기 값을 저장한다.
/// </summary>
public sealed class AiSetupWindow : Window
{
    /// <summary>좁은 폭 점검에 쓰는 창 최소 크기.</summary>
    internal const double MinimumWidth = 600;

    internal const double MinimumHeight = 440;

    private readonly AiSetupWizardViewModel _vm;
    private bool _closeDone;
    private bool _closeStarted;
    private bool _isClosed;

    public AiSetupWindow(AiSetupWizardViewModel vm)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        DataContext = vm;
        var english = vm.IsEnglish;
        Name = AiSetupIds.WindowName;
        Title = AiSetupText.WindowTitle(english);
        Width = 780;
        Height = 560;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Rail = new ItemsControl { ItemTemplate = new FuncDataTemplate<AiSetupRailItem>((item, _) => RailRow(item)) };
        Rail.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AiSetupWizardViewModel.Rail)));
        var railPanel = new Border
        {
            Width = 190,
            Padding = new Thickness(12, 16),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Caption(AiSetupWizardText.RailHeader(english)),
                    Rail,
                },
            },
        };
        railPanel.Bind(Border.BackgroundProperty, railPanel.GetResourceObservable("OrandaPanelHeaderBrush"));
        railPanel.Bind(Border.BorderBrushProperty, railPanel.GetResourceObservable("OrandaSeparatorBrush"));

        var heading = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.Bind(TextBlock.TextProperty, new Binding(nameof(AiSetupWizardViewModel.CurrentTitle)));
        StepHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        StepHost.Bind(ContentControl.ContentProperty, new Binding(nameof(AiSetupWizardViewModel.CurrentView)));
        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Margin = new Thickness(22, 18, 22, 14), Spacing = 12, Children = { heading, StepHost } },
        };

        var progress = Caption(string.Empty);
        progress.VerticalAlignment = VerticalAlignment.Center;
        progress.Bind(TextBlock.TextProperty, new Binding(nameof(AiSetupWizardViewModel.ProgressText)));
        ToolTip.SetTip(progress, AiSetupWizardText.ResumeHint(english));

        CloseButton = new Button { Content = AiSetupText.Close(english), IsCancel = true };
        CloseButton.Click += (_, _) => Close();
        BackButton = new Button { Content = AiSetupText.Back(english) };
        BackButton.Bind(IsEnabledProperty, new Binding(nameof(AiSetupWizardViewModel.CanGoBack)));
        BackButton.Click += async (_, _) => await _vm.BackAsync().ConfigureAwait(true);
        NextButton = new Button { Classes = { "accent" }, IsDefault = true };
        NextButton.Bind(ContentControl.ContentProperty, new Binding(nameof(AiSetupWizardViewModel.NextText)));
        NextButton.Bind(IsEnabledProperty, new Binding(nameof(AiSetupWizardViewModel.CanGoNext)));
        NextButton.Click += async (_, _) => await _vm.NextAsync().ConfigureAwait(true);
        foreach (var button in new[] { CloseButton, BackButton })
        {
            Avalonia.Automation.AutomationProperties.SetName(button, button.Content as string ?? string.Empty);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { CloseButton, BackButton, NextButton } };
        var foot = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 10) };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(progress);
        var footBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBorder.Bind(Border.BorderBrushProperty, footBorder.GetResourceObservable("OrandaSeparatorBrush"));

        var question = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        question.Bind(TextBlock.TextProperty, new Binding(nameof(AiSetupWizardViewModel.PendingQuestion)));
        LeaveYesButton = new Button { Content = AiSetupWizardText.LeaveYes(english) };
        LeaveYesButton.Click += (_, _) => _vm.AnswerLeave(true);
        LeaveNoButton = new Button { Content = AiSetupWizardText.LeaveNo(english), Classes = { "accent" } };
        LeaveNoButton.Click += (_, _) => _vm.AnswerLeave(false);
        var questionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { LeaveYesButton, LeaveNoButton } };
        var questionRow = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 8) };
        DockPanel.SetDock(questionButtons, Avalonia.Controls.Dock.Right);
        questionRow.Children.Add(questionButtons);
        questionRow.Children.Add(question);
        QuestionBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = questionRow };
        QuestionBar.Bind(Border.BorderBrushProperty, QuestionBar.GetResourceObservable("OrandaWarningForegroundBrush"));
        QuestionBar.Bind(IsVisibleProperty, new Binding(nameof(AiSetupWizardViewModel.PendingQuestion)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });

        var pane = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footBorder, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(QuestionBar, Avalonia.Controls.Dock.Bottom);
        pane.Children.Add(footBorder);
        pane.Children.Add(QuestionBar);
        pane.Children.Add(body);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(railPanel, Avalonia.Controls.Dock.Left);
        root.Children.Add(railPanel);
        root.Children.Add(pane);
        Content = root;

        vm.Finished += OnFinished;
        Opened += async (_, _) => await _vm.StartAsync().ConfigureAwait(true);
    }

    /// <summary>왼쪽 단계 목록(시험용).</summary>
    internal ItemsControl Rail { get; }

    /// <summary>가운데 단계 화면 자리(시험용).</summary>
    internal ContentControl StepHost { get; }

    internal Button CloseButton { get; }

    /// <summary>떠나기 질문 줄(시험용).</summary>
    internal Border QuestionBar { get; }

    internal Button LeaveYesButton { get; }

    internal Button LeaveNoButton { get; }

    internal Button BackButton { get; }

    internal Button NextButton { get; }

    /// <summary>창 틀의 뷰모델.</summary>
    internal AiSetupWizardViewModel ViewModel => _vm;

    /// <summary>왼쪽 목록 지금 단계 줄에 붙이는 클래스.</summary>
    internal const string CurrentClass = "ai-setup-current";

    /// <summary>왼쪽 목록 끝난 단계 줄에 붙이는 클래스.</summary>
    internal const string DoneClass = "ai-setup-done";

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeDone || e.Cancel)
        {
            return;
        }

        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown or WindowCloseReason.OwnerWindowClosing)
        {
            // 앱·주인 창이 닫힐 때는 막지 않는다. 떠나기·저장은 할 수 있는 만큼만 한다.
            _closeDone = true;
            _ = _vm.CloseAsync();
            return;
        }

        // 단계 떠나기·저장은 비동기라 한 번 막고, 끝난 뒤 다음 차례에 다시 닫는다(닫는 중 다시 눌러도 한 번만).
        e.Cancel = true;
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        _ = ConfirmThenCloseAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _vm.Finished -= OnFinished;
        _vm.Dispose();
        base.OnClosed(e);
    }

    private async System.Threading.Tasks.Task ConfirmThenCloseAsync()
    {
        if (!await _vm.ConfirmLeaveAsync(forClose: true).ConfigureAwait(true))
        {
            _closeStarted = false;
            return;
        }

        await CloseAfterSaveAsync().ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task CloseAfterSaveAsync()
    {
        try
        {
            await _vm.CloseAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 저장 실패여도 창은 닫는다.
        }

        _closeDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosed)
            {
                Close();
            }
        });
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        _closeDone = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosed)
            {
                Close();
            }
        });
    }

    private static Border RailRow(AiSetupRailItem item)
    {
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = item.Badge,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var title = new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
            Children = { badge, title },
        };
        Grid.SetColumn(title, 2);
        var border = new Border { Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(6), Child = row };
        switch (item.State)
        {
            case AiSetupRailState.Current:
                border.Classes.Add(CurrentClass);
                title.FontWeight = FontWeight.SemiBold;
                border.Bind(Border.BackgroundProperty, border.GetResourceObservable("OrandaTableSelectionBrush"));
                badge.Bind(Border.BorderBrushProperty, badge.GetResourceObservable("OrandaButtonAccentBrush"));
                break;
            case AiSetupRailState.Done:
                border.Classes.Add(DoneClass);
                badge.Bind(Border.BorderBrushProperty, badge.GetResourceObservable("OrandaSuccessForegroundBrush"));
                ((TextBlock)badge.Child).Bind(TextBlock.ForegroundProperty, badge.GetResourceObservable("OrandaSuccessForegroundBrush"));
                break;
            default:
                badge.Bind(Border.BorderBrushProperty, badge.GetResourceObservable("OrandaSeparatorBrush"));
                title.Classes.Add("panel-caption");
                break;
        }

        Avalonia.Automation.AutomationProperties.SetName(border, item.Number + ". " + item.Title);
        return border;
    }

    private static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("panel-caption");
        return block;
    }
}
