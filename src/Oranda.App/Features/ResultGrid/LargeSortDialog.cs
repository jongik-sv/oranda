using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 큰 결과를 정렬하려 할 때 묻는 창(TSK-13-17): ① 서버에서 ORDER BY 로 다시 조회(기본 선택) ② 전부 가져와 이 PC 에서 정렬(시간·메모리 경고) ③ 취소.
/// 서버 다시 조회를 쓸 수 없으면 ① 을 끄고 이유를 보인다. Esc 는 취소다.
/// </summary>
public sealed class LargeSortDialog : Window
{
    private readonly TaskCompletionSource<LargeSortChoice> _result = new();

    public LargeSortDialog(LargeSortRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Title = UiText.Get("LargeSort.title", "큰 결과 정렬");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var culture = CultureInfo.CurrentCulture;
        var message = UiText.Get("LargeSort.message", "{0} 열을 정렬하려면 {1}행을 넘는 결과를 모두 이 PC 로 가져와야 합니다.")
            .Replace("{0}", request.ColumnName, StringComparison.Ordinal)
            .Replace("{1}", request.Threshold.ToString("N0", culture), StringComparison.Ordinal);
        var server = new Button
        {
            Content = UiText.Get("LargeSort.server", "서버에서 ORDER BY로 다시 조회"),
            IsDefault = true,
            IsEnabled = request.ServerBlockedReason is null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var local = new Button { Content = UiText.Get("LargeSort.local", "전부 가져와 정렬"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancel = new Button { Content = UiText.Get("LargeSort.cancel", "취소"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        server.Click += (_, _) => Finish(LargeSortChoice.ServerReQuery);
        local.Click += (_, _) => Finish(LargeSortChoice.LocalAll);
        cancel.Click += (_, _) => Finish(LargeSortChoice.Cancel);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(server);
        panel.Children.Add(new TextBlock
        {
            Text = request.ServerBlockedReason ?? UiText.Get("LargeSort.serverHint", "서버가 정렬해 주므로 가장 빠르고 메모리를 적게 씁니다."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });
        panel.Children.Add(local);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("LargeSort.localWarning", "시간이 오래 걸리고 메모리를 많이 씁니다. 진행 중에는 Esc 로 멈출 수 있습니다."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });
        panel.Children.Add(cancel);
        Content = panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(LargeSortChoice.Cancel);
                e.Handled = true;
            }
        };
        Closed += (_, _) => _result.TrySetResult(LargeSortChoice.Cancel); // 창을 그냥 닫아도 취소
    }

    /// <summary>선택을 기다린다. 창을 닫으면 취소.</summary>
    public Task<LargeSortChoice> ChoiceAsync => _result.Task;

    private void Finish(LargeSortChoice choice)
    {
        _result.TrySetResult(choice);
        Close();
    }

    /// <summary>owner 위에 모달로 띄워 선택을 돌려준다. owner 가 없으면 그냥 띄운다.</summary>
    public static async Task<LargeSortChoice> AskAsync(Window? owner, LargeSortRequest request, CancellationToken ct)
    {
        var dialog = new LargeSortDialog(request);
        using var registration = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        if (owner is { IsVisible: true })
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await dialog.ChoiceAsync.ConfigureAwait(true);
    }
}
