using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 코드로 만드는 작은 대화상자(<c>AvaloniaExecutionDialogs</c>·<c>AvaloniaEditorDialogs</c>)가 함께 쓰는 도우미:
/// 주 창 찾기, 모달 또는 독립 창으로 띄우기, 본문 틀, 오른쪽 정렬 단추 줄, 자동화 이름.
/// </summary>
internal static class DialogShell
{
    /// <summary>데스크톱 수명 주기의 주 창(없으면 null: 시험·시작 직후).</summary>
    internal static Window? MainWindow
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>
    /// 창을 닫을 때까지 기다린다. 보이는 <paramref name="owner"/> 가 있으면 그 위에 모달로, 없으면(시험·시작 직후) 화면 가운데 독립 창으로 띄운다.
    /// </summary>
    internal static async Task ShowAsync(Window dialog, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
            return;
        }

        // 주 창이 없으면(시험·시작 직후) 독립 창으로 띄우고 닫힐 때까지 기다린다.
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        await closed.Task.ConfigureAwait(true);
    }

    /// <summary>본문 위, 단추 줄 아래의 세로 틀(바깥 여백 16, 간격 12).</summary>
    internal static StackPanel Framed(Control content, Control buttons)
        => new() { Margin = new Thickness(16), Spacing = 12, Children = { content, buttons } };

    /// <summary>오른쪽 정렬 가로 단추 줄(간격 8).</summary>
    internal static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            panel.Children.Add(b);
        }

        return panel;
    }

    internal static void AutomationName(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);
}
