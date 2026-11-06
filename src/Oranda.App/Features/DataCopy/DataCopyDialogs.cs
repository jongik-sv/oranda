using Avalonia.Controls;
using Avalonia.Media;
using Oranda.App.Features.SqlEditor;

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

    private bool OwnsDialogs => fallback() is AvaloniaEditorDialogs && owner.IsVisible;

    private async Task<T> ShowAsync<T>(string title, Control content, Func<bool, T> result)
    {
        var accepted = false;
        var dialog = AvaloniaEditorDialogs.BuildDialog(title, content, null, "", () => accepted = true);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result(accepted);
    }
}
