using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.Rollback;

/// <summary>원복 스크립트 탭이 묻는 대화상자(화면 구현은 <see cref="AvaloniaRollbackScriptDialogs"/>, 시험은 가짜).</summary>
public interface IRollbackScriptDialogs
{
    /// <summary>예·취소 확인. 기본 단추는 「취소」. true = 진행.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct);

    /// <summary>내보낼 <c>.sql</c> 파일 경로를 고른다. null = 취소.</summary>
    Task<string?> PickExportPathAsync(string suggestedName, CancellationToken ct);
}

/// <summary>원복 스크립트 대화상자 자리. 시험은 <see cref="Current"/> 를 가짜로 바꾼다.</summary>
public static class RollbackScriptDialogs
{
    private static IRollbackScriptDialogs? s_current;

    public static IRollbackScriptDialogs Current
    {
        get => s_current ??= new AvaloniaRollbackScriptDialogs();
        set => s_current = value;
    }
}

/// <summary>주 창 위에 띄우는 화면 구현.</summary>
internal sealed class AvaloniaRollbackScriptDialogs : IRollbackScriptDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct) => OnUi(async () =>
    {
        var accepted = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            MinWidth = 320,
            ShowInTaskbar = false,
        };
        var ok = new Button { Content = okText, MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = DialogShell.Framed(
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left },
            DialogShell.Buttons(ok, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        await DialogShell.ShowAsync(dialog, DialogShell.MainWindow).ConfigureAwait(true);
        return accepted;
    });

    public Task<string?> PickExportPathAsync(string suggestedName, CancellationToken ct) => OnUi(async () =>
    {
        if (DialogShell.MainWindow?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "원복 스크립트 내보내기",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    });

    private static Task<T> OnUi<T>(Func<Task<T>> body) =>
        Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.InvokeAsync(body);
}
