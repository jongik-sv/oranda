using Avalonia.Controls;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사 창이 묻는 이름 입력·예/아니오 대화상자. 앱 대화상자(<see cref="AvaloniaEditorDialogs"/>)는 주 창을 소유자로 띄워
/// 닫히면 주 창이 앞으로 나와 복사 창을 덮는다. 그래서 같은 모양의 대화상자를 복사 창을 소유자로 띄운다.
/// 앱 대화상자가 아닌 구현(시험의 가짜)이 걸려 있으면 그쪽에 그대로 맡긴다.
/// </summary>
internal sealed class DataCopyDialogs(Window owner, Func<IEditorDialogs> fallback)
{
    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct)
    {
        if (!OwnsDialogs)
        {
            return fallback().PromptAsync(title, initial, ct);
        }

        var box = new TextBox { Text = initial, MinWidth = 280 };
        return ShowAsync<string?>(title, box, ok => ok ? box.Text : null);
    }

    public Task<bool> ConfirmAsync(string message, CancellationToken ct)
    {
        if (!OwnsDialogs)
        {
            return fallback().ConfirmAsync(message, ct);
        }

        return ShowAsync("Oranda", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, ok => ok);
    }

    /// <summary>
    /// 「복사 실행」 확인(결정 B-2 a): 대상 계정별 지우고 넣을 표 수·처리 종류를 보이고 묻는다. 기본 단추(Enter)·처음 포커스는 「취소」다.
    /// </summary>
    public async Task<bool> ConfirmRunAsync(RunConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!OwnsDialogs)
        {
            var text = confirmation.Header + "\n" + string.Join("\n", confirmation.Owners.Select(RunConfirmation.LineText));
            return await fallback().ConfirmAsync(text, CancellationToken.None).ConfigureAwait(true);
        }

        var dialog = BuildRunConfirmDialog(confirmation, out var accepted);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return accepted();
    }

    /// <summary>실행 확인 대화상자 창(화면 시험이 그대로 그려 본다). <paramref name="accepted"/> 는 「복사 실행」을 눌렀는지.</summary>
    internal static Window BuildRunConfirmDialog(RunConfirmation confirmation, out Func<bool> accepted)
    {
        var ok = false;
        accepted = () => ok;
        var dialog = new Window
        {
            Title = DataCopyText.L("복사 실행 확인"),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var lines = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        foreach (var line in confirmation.Owners)
        {
            var row = new TextBlock { Text = RunConfirmation.LineText(line), FontFamily = (FontFamily?)Avalonia.Application.Current?.FindResource("OrandaCodeFontFamily") ?? FontFamily.Default };
            if (!line.IsSessionUser)
            {
                row.Classes.Add("warning-text");
            }

            lines.Children.Add(row);
        }

        var body = new StackPanel
        {
            Spacing = 4,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = confirmation.Header, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                lines,
                new TextBlock
                {
                    Text = DataCopyText.L("사전 점검에서 본 DELETE·MERGE 문장 그대로 실행합니다. 계정 이름이 맞는지 확인하세요."),
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Avalonia.Thickness(0, 6, 0, 0),
                },
            },
        };
        var run = new Button { Content = DataCopyText.L("복사 실행"), MinWidth = 80 };
        var cancel = new Button { Content = UiText.Get("Shell.dialog.cancel", "취소"), MinWidth = 80, IsDefault = true, IsCancel = true, Classes = { "accent" } };
        Avalonia.Automation.AutomationProperties.SetName(run, DataCopyText.L("복사 실행"));
        run.Click += (_, _) =>
        {
            ok = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = DialogShell.Framed(body, DialogShell.Buttons(run, cancel));
        dialog.Opened += (_, _) => cancel.Focus(); // 실수로 Enter 를 눌러도 실행하지 않는다
        return dialog;
    }

    private bool OwnsDialogs => fallback() is AvaloniaEditorDialogs && owner.IsVisible;

    private async Task<T> ShowAsync<T>(string title, Control content, Func<bool, T> result)
    {
        var accepted = false;
        var dialog = AvaloniaEditorDialogs.BuildDialog(title, content, null, "", () => accepted = true);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result(accepted);
    }
}
