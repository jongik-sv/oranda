using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 접속 관리자 대화상자(도킹 안 함, ui-shell.md 2장). 접속 트리 + 프로필 편집기 + 지갑 zip 끌어 놓기.
/// 비밀번호 묻기·변경·삭제 확인은 <see cref="ConnectionDialogs"/> 의 작은 대화상자를 쓴다.
/// </summary>
public partial class ConnectionManagerWindow : Window
{
    public ConnectionManagerWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 목록 단축키(SQL Developer·Toad 관례). 터널로 먼저 받아 Enter 가 기본 단추(접속)로 한 번 더 가지 않게 한다.
        ConnectionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        ConnectionTree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConnectionTree.DoubleTapped += OnTreeDoubleTapped;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionManagerViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
                vm.AskPassword ??= profile => ConnectionDialogs.PromptPasswordAsync(this, profile);
                vm.AskChangePassword ??= (profile, typed) => ConnectionDialogs.ChangePasswordAsync(this, profile, typed);
                vm.Confirm ??= message => ConnectionDialogs.ConfirmAsync(this, message);
                vm.CopyText ??= CopyToClipboardAsync;
            }
        };
    }

    private ConnectionManagerViewModel? Model => DataContext as ConnectionManagerViewModel;

    private async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    /// <summary>Enter = 접속(폴더면 펼치기/접기), Delete = 삭제(확인), F2 = 이름 칸으로.</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when vm.Selected is { IsFolder: true } folder:
                folder.IsExpanded = !folder.IsExpanded;
                e.Handled = true;
                break;
            case Key.Enter:
                Run(vm.ConnectCommand);
                e.Handled = true;
                break;
            case Key.Delete:
                Run(vm.DeleteCommand);
                e.Handled = true;
                break;
            case Key.F2:
                FocusNameBox();
                e.Handled = true;
                break;
        }
    }

    /// <summary>오른쪽 단추는 누른 줄을 먼저 고른다(오른쪽 클릭 메뉴가 그 접속에 대해 열린다).</summary>
    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } vm && e.GetCurrentPoint(ConnectionTree).Properties.IsRightButtonPressed
            && (e.Source as Control)?.DataContext is ConnectionTreeNode { IsFolder: false } node)
        {
            vm.Selected = node;
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Model is { } vm && (e.Source as Control)?.DataContext is ConnectionTreeNode { IsFolder: false })
        {
            Run(vm.ConnectCommand);
        }
    }

    /// <summary>이름 칸으로 초점을 옮기고 글자를 모두 고른다(F2).</summary>
    public void FocusNameBox()
    {
        if (EditorView.FindControl<TextBox>("NameBox") is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>끌어 놓은 파일 가운데 zip 만 허용한다.</summary>
    private static string? FirstZip(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && string.Equals(Path.GetExtension(p), ".zip", StringComparison.OrdinalIgnoreCase));

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = FirstZip(e) is null ? DragDropEffects.None : DragDropEffects.Copy;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (FirstZip(e) is { } zip && DataContext is ConnectionManagerViewModel vm)
        {
            e.Handled = true;
            await vm.ImportWalletAsync(zip).ConfigureAwait(true);
        }
    }
}

/// <summary>접속 관리자가 쓰는 작은 모달 대화상자(코드로 만든다: 입력칸 1~3개).</summary>
public static class ConnectionDialogs
{
    /// <summary>비밀번호 한 개를 묻는다. 취소하면 null.</summary>
    public static async Task<string?> PromptPasswordAsync(Window owner, Oranda.Core.Connection.ConnectionProfile profile)
    {
        var box = new TextBox { PasswordChar = '●', MinWidth = 260 };
        Avalonia.Automation.AutomationProperties.SetName(box, "비밀번호");
        var ok = new Button { Content = "접속", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var window = Build(
            $"{profile.Name} 비밀번호",
            [
                new TextBlock { Text = $"{profile.User}@{profile.DataSource}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                box,
            ],
            ok,
            cancel);
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => box.Focus();
        await ShowAsync(window, owner).ConfigureAwait(true);
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>비밀번호 변경(현재·새·새 확인). 취소하면 null. 현재 비밀번호를 비우면 저장된 값을 쓴다.</summary>
    public static async Task<(string? Old, string New)?> ChangePasswordAsync(Window owner, Oranda.Core.Connection.ConnectionProfile profile, string? typedOld)
    {
        var oldBox = new TextBox { PasswordChar = '●', MinWidth = 260, Text = typedOld, PlaceholderText = "현재 비밀번호" };
        var newBox = new TextBox { PasswordChar = '●', MinWidth = 260, PlaceholderText = "새 비밀번호" };
        var confirmBox = new TextBox { PasswordChar = '●', MinWidth = 260, PlaceholderText = "새 비밀번호 확인" };
        Avalonia.Automation.AutomationProperties.SetName(oldBox, "현재 비밀번호");
        Avalonia.Automation.AutomationProperties.SetName(newBox, "새 비밀번호");
        Avalonia.Automation.AutomationProperties.SetName(confirmBox, "새 비밀번호 확인");
        var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.IndianRed };
        var ok = new Button { Content = "변경", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var window = Build(
            $"{profile.Name} 비밀번호 변경",
            [
                new TextBlock { Text = $"{profile.User} 의 비밀번호를 바꿉니다.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                oldBox,
                newBox,
                confirmBox,
                message,
            ],
            ok,
            cancel);
        (string? Old, string New)? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(newBox.Text))
            {
                message.Text = "새 비밀번호를 입력하세요.";
                return;
            }

            if (!string.Equals(newBox.Text, confirmBox.Text, StringComparison.Ordinal))
            {
                message.Text = "새 비밀번호가 서로 다릅니다.";
                return;
            }

            result = (string.IsNullOrEmpty(oldBox.Text) ? null : oldBox.Text, newBox.Text);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => (string.IsNullOrEmpty(oldBox.Text) ? oldBox : newBox).Focus();
        await ShowAsync(window, owner).ConfigureAwait(true);
        return result;
    }

    /// <summary>예/아니오 확인.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message)
    {
        var yes = new Button { Content = "예", MinWidth = 80, IsDefault = true };
        var no = new Button { Content = "아니요", MinWidth = 80, IsCancel = true };
        var window = Build("확인", [new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 }], yes, no);
        var answer = false;
        yes.Click += (_, _) =>
        {
            answer = true;
            window.Close();
        };
        no.Click += (_, _) => window.Close();
        await ShowAsync(window, owner).ConfigureAwait(true);
        return answer;
    }

    /// <summary>알림 한 줄(확인 단추 하나).</summary>
    public static async Task MessageAsync(Window owner, string message)
    {
        var ok = new Button { Content = "확인", MinWidth = 80, IsDefault = true, IsCancel = true };
        var window = Build("접속", [new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 }], ok, new Button { IsVisible = false });
        ok.Click += (_, _) => window.Close();
        await ShowAsync(window, owner).ConfigureAwait(true);
    }

    private static Window Build(string title, Control[] body, Button primary, Button secondary)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10 };
        foreach (var c in body)
        {
            panel.Children.Add(c);
        }

        panel.Children.Add(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { primary, secondary },
        });
        return new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 320,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
    }

    private static Task ShowAsync(Window window, Window owner) =>
        owner.IsVisible ? window.ShowDialog(owner) : ShowStandalone(window);

    private static async Task ShowStandalone(Window window)
    {
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closed.Task.ConfigureAwait(true);
    }
}
