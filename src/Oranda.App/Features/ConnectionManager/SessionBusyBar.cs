using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Icons;
using AvDock = Avalonia.Controls.Dock;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// SQL 작업 공간 위 안내 막대(TSK-14-03, D-036): 함께 쓰는 세션이 다른 작업 공간에서 실행 중일 때 실행을 누르면
/// 「이 세션은 ○○ 워크스페이스에서 실행 중 — [새 세션에서 실행] [기다리기]」. 내용은 작업 공간의 <see cref="SqlWorkspaceViewModel.BusyNotice"/>
/// (세션 라우터가 넣고 지운다)이고, 없으면 숨는다. 모양은 매퍼 충돌 띠와 같다(경고 아이콘 + 줄바꿈 글 + 단추, 바탕·선 색은 테마 리소스).
/// </summary>
public sealed class SessionBusyBar : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _runInNew;
    private readonly Button _wait;
    private SqlWorkspaceViewModel? _workspace;

    public SessionBusyBar()
    {
        _runInNew = Make("run", "Execution.busyBar.runInNew", "새 세션에서 실행",
            "이 워크스페이스를 같은 DB 의 새 세션으로 옮기고 바로 실행합니다. 지금 세션의 트랜잭션은 옮겨지지 않습니다.");
        _wait = Make("clear", "Execution.busyBar.wait", "기다리기", "안내를 닫습니다. 앞 실행이 끝나면 다시 실행하세요.");
        _runInNew.Click += async (_, _) =>
        {
            if (!_runInNew.IsEnabled || _workspace?.BusyNotice is not { } notice)
            {
                return;
            }

            _runInNew.IsEnabled = false; // 누르는 즉시 끈다: 여는 사이 또 눌러 세션 둘·같은 SQL 두 번이 되지 않게(TSK-14-07)
            try
            {
                await notice.RunInNewSession().ConfigureAwait(true);
            }
            finally
            {
                _runInNew.IsEnabled = true; // 못 열었으면 막대가 남아 다시 누를 수 있다(옮겼으면 막대가 닫힌다)
            }
        };
        _wait.Click += (_, _) => _workspace?.BusyNotice?.Wait();

        HorizontalAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(8, 3);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Bind(BackgroundProperty, this.GetResourceObservable("OrandaPanelHeaderBrush"));
        Bind(BorderBrushProperty, this.GetResourceObservable("OrandaWarningForegroundBrush"));

        var dock = new DockPanel { LastChildFill = true };
        if (ShellIcons.Create("warn") is { } icon)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, AvDock.Left);
            dock.Children.Add(icon);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _runInNew, _wait } };
        DockPanel.SetDock(buttons, AvDock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(_text);
        Child = dock;
        Avalonia.Automation.AutomationProperties.SetName(this, UiText.Get("Execution.busyBar.name", "세션 실행 중 안내"));
        // 도킹이 View 를 떼었다 다시 붙이므로 붙어 있는 동안만 작업 공간을 본다(떨어진 막대가 작업 공간을 붙잡지 않게).
        DataContextChanged += (_, _) => Watch(IsAttached ? DataContext as SqlWorkspaceViewModel : null);
        AttachedToVisualTree += (_, _) =>
        {
            IsAttached = true;
            Watch(DataContext as SqlWorkspaceViewModel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            IsAttached = false;
            Watch(null);
        };
        Refresh();
    }

    private bool IsAttached { get; set; }

    /// <summary>「새 세션에서 실행」 단추(시험이 누른다).</summary>
    public Button RunInNewButton => _runInNew;

    /// <summary>「기다리기」 단추(시험이 누른다).</summary>
    public Button WaitButton => _wait;

    /// <summary>막대 글(시험이 읽는다).</summary>
    public string? Text => _text.Text;

    private void Watch(SqlWorkspaceViewModel? workspace)
    {
        if (ReferenceEquals(_workspace, workspace))
        {
            return;
        }

        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspaceChanged;
        }

        _workspace = workspace;
        if (workspace is not null)
        {
            workspace.PropertyChanged += OnWorkspaceChanged;
        }

        Refresh();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.BusyNotice))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var notice = _workspace?.BusyNotice;
        IsVisible = notice is not null;
        _text.Text = notice?.Text;
    }

    private static Button Make(string iconKey, string key, string fallbackLabel, string fallbackTip)
    {
        var label = UiText.Get(key, fallbackLabel);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (ShellIcons.Create(iconKey) is { } icon)
        {
            panel.Children.Add(icon);
        }

        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Padding = new Thickness(6, 3), Content = panel };
        button.Classes.Add("tool");
        ToolTip.SetTip(button, UiText.Get(key + ".tip", fallbackTip));
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }
}
