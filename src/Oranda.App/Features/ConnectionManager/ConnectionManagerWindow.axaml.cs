using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionManagerViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
                vm.AskPassword ??= profile => ConnectionDialogs.PromptPasswordAsync(this, profile);
                vm.AskChangePassword ??= (profile, typed) => ConnectionDialogs.ChangePasswordAsync(this, profile, typed);
                vm.Confirm ??= message => ConnectionDialogs.ConfirmAsync(this, message);
            }
        };
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
