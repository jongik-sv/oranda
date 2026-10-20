using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 목록 단축키(SQL Developer·Toad 관례). 터널로 먼저 받아 Enter 가 기본 단추(접속)로 한 번 더 가지 않게 한다.
        ConnectionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        ConnectionTree.AddHandler(PointerPressedEvent, OnTreePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConnectionTree.AddHandler(PointerMovedEvent, OnTreeMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConnectionTree.AddHandler(PointerReleasedEvent, (_, _) => _pressNode = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        ConnectionTree.AddHandler(LostFocusEvent, OnTreeLostFocus);
        ConnectionTree.DoubleTapped += OnTreeDoubleTapped;
        Closing += OnClosing;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConnectionManagerViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
                vm.AskPassword ??= profile => ConnectionDialogs.PromptPasswordAsync(this, profile);
                vm.AskChangePassword ??= (profile, typed) => ConnectionDialogs.ChangePasswordAsync(this, profile, typed);
                vm.Confirm ??= message => ConnectionDialogs.ConfirmAsync(this, message);
                vm.AskUnsaved ??= message => ConnectionDialogs.AskUnsavedAsync(this, message);
                vm.CopyText ??= CopyToClipboardAsync;
                vm.AskFolder ??= (folders, current) => ConnectionDialogs.PickFolderAsync(this, folders, current);
                vm.FolderEditRequested += (_, node) => FocusFolderEditor(node, retry: true);
            }
        };
    }

    private ConnectionManagerViewModel? Model => DataContext as ConnectionManagerViewModel;

    /// <summary>저장 확인을 마치고 닫는 중(Closing 을 다시 막지 않는다).</summary>
    private bool _closeConfirmed;

    /// <summary>저장하지 않은 편집이 있으면 닫기를 멈추고 저장·버리기·취소를 묻는다. 앱 종료 때는 묻지 않는다.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Model is not { Editor.IsDirty: true } vm
            || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            return;
        }

        e.Cancel = true;
        if (await vm.ResolveUnsavedAsync().ConfigureAwait(true))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Enter = 접속(폴더면 펼치기/접기), Delete = 삭제(확인), F2 = 이름 칸으로(폴더면 폴더 이름 편집).
    /// 폴더 이름 입력칸에서는 Enter = 확정, Esc = 취소만 받는다.
    /// </summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Source is TextBox box)
        {
            if (box.DataContext is ConnectionTreeNode { IsEditing: true } editing && e.Key is Key.Enter or Key.Escape)
            {
                if (e.Key == Key.Enter)
                {
                    _ = vm.CommitFolderEditAsync(editing);
                }
                else
                {
                    ConnectionManagerViewModel.CancelFolderEdit(editing);
                }

                e.Handled = true;
                ConnectionTree.Focus();
            }

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
            case Key.F2 when vm.Selected is { IsFolder: true }:
                Run(vm.RenameFolderCommand);
                e.Handled = true;
                break;
            case Key.F2:
                FocusNameBox();
                e.Handled = true;
                break;
        }
    }

    /// <summary>줄 끌기를 시작하려고 누른 접속·폴더 줄과 그 눌림(끌기 시작에 필요하다).</summary>
    private ConnectionTreeNode? _pressNode;

    private PointerPressedEventArgs? _pressArgs;
    private Avalonia.Point _pressPoint;

    /// <summary>지금 끌고 있는 줄(파일 끌기와 구분한다).</summary>
    private ConnectionTreeNode? _dragNode;

    private ConnectionTreeNode? _hoverFolder;

    /// <summary>오른쪽 단추는 누른 줄을 먼저 고른다(오른쪽 클릭 메뉴가 그 줄에 대해 열린다). 왼쪽 단추는 줄 끌기의 시작점이 된다.</summary>
    private void OnTreePressed(object? sender, PointerPressedEventArgs e)
    {
        var node = (e.Source as Control)?.DataContext as ConnectionTreeNode;
        var point = e.GetCurrentPoint(ConnectionTree);
        if (Model is { } vm && point.Properties.IsRightButtonPressed && node is not null)
        {
            vm.Selected = node;
        }

        _pressNode = point.Properties.IsLeftButtonPressed && node is { IsEditing: false } && e.Source is not TextBox ? node : null;
        _pressArgs = e;
        _pressPoint = e.GetPosition(ConnectionTree);
    }

    /// <summary>눌린 채로 조금 움직이면 줄 끌기를 시작한다(접속·폴더를 폴더 위로).</summary>
    private async void OnTreeMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is not null || _pressNode is not { } node || _pressArgs is null || !e.GetCurrentPoint(ConnectionTree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var d = e.GetPosition(ConnectionTree) - _pressPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6)
        {
            return;
        }

        var args = _pressArgs;
        _pressNode = null;
        _dragNode = node;
        try
        {
            var data = new Avalonia.Input.DataTransfer();
            data.Add(DataTransferItem.CreateText(node.Title));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Move).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // async void 라서 여기서 못 잡으면 앱이 죽는다(Windows OLE 는 COMException 을 던진다, TSK-07-05).
            Model?.ReportError("끌어 놓기를 시작하지 못했습니다: " + ex.Message);
        }
        finally
        {
            _dragNode = null;
            SetHoverFolder(null);
        }
    }

    /// <summary>끌어 놓을 폴더 경로. 폴더 줄 = 그 폴더, 접속 줄 = 그 접속의 폴더, 목록의 빈 곳 = 맨 위. 목록 밖이면 놓을 수 없다.</summary>
    private bool TryGetDropFolder(DragEventArgs e, out string? folder)
    {
        folder = null;
        var p = e.GetPosition(ConnectionTree);
        if (p.X < 0 || p.Y < 0 || p.X > ConnectionTree.Bounds.Width || p.Y > ConnectionTree.Bounds.Height)
        {
            return false;
        }

        switch ((e.Source as Control)?.DataContext)
        {
            case ConnectionTreeNode { IsFolder: true } f:
                folder = f.Path;
                break;
            case ConnectionTreeNode { Profile: { } profile }:
                folder = Oranda.Core.Connection.ConnectionFolders.Normalize(profile.Group);
                break;
        }

        return true;
    }

    private void SetHoverFolder(ConnectionTreeNode? folder)
    {
        if (ReferenceEquals(_hoverFolder, folder))
        {
            return;
        }

        if (_hoverFolder is not null)
        {
            _hoverFolder.IsDropTarget = false;
        }

        _hoverFolder = folder;
        if (folder is not null)
        {
            folder.IsDropTarget = true;
        }
    }

    /// <summary>폴더 이름 입력칸이 초점을 잃으면 확정한다(Esc 로 취소했으면 이미 편집이 끝나 있다).</summary>
    private void OnTreeLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Model is { } vm && e.Source is TextBox { DataContext: ConnectionTreeNode { IsEditing: true } node })
        {
            _ = vm.CommitFolderEditAsync(node);
        }
    }

    /// <summary>폴더 이름 입력칸에 초점을 주고 글자를 모두 고른다. 줄이 아직 그려지지 않았으면 한 번 더 기다린다.</summary>
    private void FocusFolderEditor(ConnectionTreeNode node, bool retry)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var box = (ConnectionTree.TreeContainerFromItem(node) as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == "FolderNameBox");
                if (box is not null && box.IsEffectivelyVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
                else if (retry && node.IsEditing)
                {
                    FocusFolderEditor(node, retry: false);
                }
            },
            DispatcherPriority.Loaded);
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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_dragNode is { } node)
        {
            var ok = TryGetDropFolder(e, out var folder) && ConnectionManagerViewModel.CanDrop(node, folder);
            SetHoverFolder(ok && folder is not null ? Model?.FindFolder(folder) : null);
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = FirstZip(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetHoverFolder(null);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragNode is { } node && Model is { } moving)
        {
            e.Handled = true;
            SetHoverFolder(null);
            if (TryGetDropFolder(e, out var folder))
            {
                await moving.MoveNodeAsync(node, folder).ConfigureAwait(true);
            }

            return;
        }

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

    /// <summary>
    /// 폴더 목록에서 폴더 하나를 고른다(맨 위에 「(폴더 없음)」). 취소하면 null.
    /// </summary>
    public static async Task<FolderChoice?> PickFolderAsync(Window owner, IReadOnlyList<string> folders, string? current)
    {
        const string None = "(폴더 없음)";
        var list = new ListBox { ItemsSource = new[] { None }.Concat(folders).ToList(), MinWidth = 280, MaxHeight = 280 };
        Avalonia.Automation.AutomationProperties.SetName(list, "폴더 목록");
        list.SelectedItem = current is not null && folders.Contains(current, StringComparer.OrdinalIgnoreCase) ? folders.First(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase)) : None;
        var ok = new Button { Content = "이동", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var window = Build("폴더로 이동", [new TextBlock { Text = "옮길 폴더를 고르세요." }, list], ok, cancel);
        FolderChoice? result = null;
        void Accept()
        {
            if (list.SelectedItem is string picked)
            {
                result = new FolderChoice(picked == None ? null : picked);
                window.Close();
            }
        }

        ok.Click += (_, _) => Accept();
        list.DoubleTapped += (_, _) => Accept();
        cancel.Click += (_, _) => window.Close();
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

    /// <summary>저장하지 않은 편집을 어떻게 할지 묻는다(저장·버리기·취소). 창을 그냥 닫으면 취소.</summary>
    public static async Task<UnsavedChoice> AskUnsavedAsync(Window owner, string message)
    {
        var save = new Button { Content = "저장", MinWidth = 80, IsDefault = true };
        var discard = new Button { Content = "버리기", MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        var window = Build("저장하지 않은 변경", [new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 }], save, discard);
        ((StackPanel)((StackPanel)window.Content!).Children[^1]).Children.Add(cancel);
        var answer = UnsavedChoice.Cancel;
        save.Click += (_, _) =>
        {
            answer = UnsavedChoice.Save;
            window.Close();
        };
        discard.Click += (_, _) =>
        {
            answer = UnsavedChoice.Discard;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
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
