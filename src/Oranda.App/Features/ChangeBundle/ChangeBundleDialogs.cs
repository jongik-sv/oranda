using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>문장 전문 창의 결과. <see cref="Text"/> = 고친 글(안 고쳤으면 null), <see cref="OpenInEditor"/> = 「편집기에서 열기」를 눌렀다.</summary>
public sealed record ChangeBundleTextEdit(string? Text, bool OpenInEditor);

/// <summary>반영 묶음 창이 띄우는 대화상자(화면 구현은 <see cref="AvaloniaChangeBundleDialogs"/>, 시험은 가짜).</summary>
public interface IChangeBundleDialogs
{
    /// <summary>예·취소 확인. 기본 단추는 「취소」. true = 진행.</summary>
    Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct);

    /// <summary>한 줄 입력(묶음 이름·대상 소유자). null = 취소.</summary>
    Task<string?> PromptAsync(string title, string label, string initial, CancellationToken ct);

    /// <summary>문장 전문 보기·고치기. null = 닫기(아무것도 안 함).</summary>
    Task<ChangeBundleTextEdit?> EditTextAsync(string title, string text, CancellationToken ct);

    /// <summary>알림(확인 단추 하나).</summary>
    Task NoticeAsync(string title, string message, CancellationToken ct);

    /// <summary>「실행…」 창(접속 고르기·소유자 바꾸기·실행). 닫힐 때까지 기다린다.</summary>
    Task ShowRunAsync(ChangeBundleRunViewModel vm, CancellationToken ct);

    /// <summary>「의뢰용 스크립트 저장…」 창(소유자 바꾸기·저장). 닫힐 때까지 기다린다.</summary>
    Task ShowSaveAsync(ChangeBundleSaveViewModel vm, CancellationToken ct);

    /// <summary>저장할 <c>.sql</c> 경로. null = 취소.</summary>
    Task<string?> PickSavePathAsync(string suggestedName, CancellationToken ct);
}

/// <summary>반영 묶음 대화상자 자리. 시험은 <see cref="Current"/> 를 가짜로 바꾼다.</summary>
public static class ChangeBundleDialogs
{
    private static IChangeBundleDialogs? s_current;

    public static IChangeBundleDialogs Current
    {
        get => s_current ??= new AvaloniaChangeBundleDialogs();
        set => s_current = value;
    }
}

/// <summary>주 창 위에 띄우는 화면 구현.</summary>
internal sealed class AvaloniaChangeBundleDialogs : IChangeBundleDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct) => OnUi(async () =>
    {
        var accepted = false;
        var dialog = NewDialog(title);
        var ok = new Button { Content = okText, MinWidth = 80 };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = DialogShell.Framed(Message(message), DialogShell.Buttons(ok, cancel));
        dialog.Opened += (_, _) => cancel.Focus();
        await DialogShell.ShowAsync(dialog, DialogShell.MainWindow).ConfigureAwait(true);
        return accepted;
    });

    public Task<string?> PromptAsync(string title, string label, string initial, CancellationToken ct) => OnUi(async () =>
    {
        string? result = null;
        var dialog = NewDialog(title);
        var box = new TextBox { Text = initial, MinWidth = 360 };
        DialogShell.AutomationName(box, label);
        var ok = new Button { Content = "확인", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result = box.Text ?? string.Empty;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label });
        body.Children.Add(box);
        dialog.Content = DialogShell.Framed(body, DialogShell.Buttons(ok, cancel));
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await DialogShell.ShowAsync(dialog, DialogShell.MainWindow).ConfigureAwait(true);
        return result;
    });

    public Task<ChangeBundleTextEdit?> EditTextAsync(string title, string text, CancellationToken ct) => OnUi(async () =>
    {
        ChangeBundleTextEdit? result = null;
        var dialog = NewDialog(title);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.CanResize = true;
        dialog.Width = 720;
        dialog.Height = 440;
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            Height = 320,
        };
        DialogShell.AutomationName(box, "문장 전문");
        var save = new Button { Content = "고치기", MinWidth = 80 };
        var open = new Button { Content = "편집기에서 열기", MinWidth = 80 };
        var cancel = new Button { Content = "닫기", MinWidth = 80, IsDefault = true, IsCancel = true };
        save.Click += (_, _) =>
        {
            var edited = box.Text ?? string.Empty;
            result = new ChangeBundleTextEdit(string.Equals(edited, text, StringComparison.Ordinal) ? null : edited, false);
            dialog.Close();
        };
        open.Click += (_, _) =>
        {
            result = new ChangeBundleTextEdit(null, true);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = "고치면 이 문장의 실행 표시(개발·테스트·운영)는 지워진다.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        dialog.Content = DialogShell.Framed(body, DialogShell.Buttons(save, open, cancel));
        await DialogShell.ShowAsync(dialog, DialogShell.MainWindow).ConfigureAwait(true);
        return result;
    });

    public Task NoticeAsync(string title, string message, CancellationToken ct) => OnUi(async () =>
    {
        var dialog = NewDialog(title);
        var ok = new Button { Content = "확인", MinWidth = 80, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = DialogShell.Framed(Message(message), DialogShell.Buttons(ok));
        await DialogShell.ShowAsync(dialog, DialogShell.MainWindow).ConfigureAwait(true);
        return true;
    });

    public Task ShowRunAsync(ChangeBundleRunViewModel vm, CancellationToken ct) => OnUi(async () =>
    {
        var window = new ChangeBundleRunWindow { DataContext = vm };
        await DialogShell.ShowAsync(window, DialogShell.MainWindow).ConfigureAwait(true);
        return true;
    });

    public Task ShowSaveAsync(ChangeBundleSaveViewModel vm, CancellationToken ct) => OnUi(async () =>
    {
        var window = new ChangeBundleSaveWindow { DataContext = vm };
        await DialogShell.ShowAsync(window, DialogShell.MainWindow).ConfigureAwait(true);
        return true;
    });

    public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken ct) => OnUi(async () =>
    {
        if (DialogShell.MainWindow?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "의뢰용 스크립트 저장",
            SuggestedFileName = suggestedName,
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    });

    private static Window NewDialog(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        CanResize = false,
        MinWidth = 320,
        ShowInTaskbar = false,
    };

    private static TextBlock Message(string message) =>
        new() { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };

    private static Task<T> OnUi<T>(Func<Task<T>> body) =>
        Dispatcher.UIThread.CheckAccess() ? body() : Dispatcher.UIThread.InvokeAsync(body);
}
