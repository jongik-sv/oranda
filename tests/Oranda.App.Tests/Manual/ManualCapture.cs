using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 화면 캡처 도우미. 실제 View 를 Avalonia 헤드리스(Skia)로 그려 PNG 로 쓴다.
/// 평소 시험·CI 에서는 파일을 쓰지 않는다. 켜는 방법(둘 다 필요):
/// <c>ORANDA_MANUAL_CAPTURE=1</c> 과 <c>ORANDA_RENDER_DIR=&lt;저장소 밖 폴더&gt;</c>(후자는 헤드리스가 실제 픽셀을 그리게 하는 기존 스위치).
/// 출력 폴더 기본값은 <c>site/src/assets/screens</c>, <c>ORANDA_MANUAL_OUT</c> 으로 바꾼다.
/// </summary>
internal static class ManualCapture
{
    /// <summary>번호 표시 한 개: 설명할 컨트롤과 번호. 위치는 컨트롤의 화면 좌표에서 계산한다.</summary>
    internal sealed record Callout(Control Target, int Number);

    /// <summary>캡처를 쓸 수 있는지(생성기 켜짐 + 실제 픽셀 그리기).</summary>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("ORANDA_MANUAL_CAPTURE") == "1"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR"));

    /// <summary>한 장의 크기 상한(바이트). 매뉴얼 이미지는 이 값 이하여야 한다.</summary>
    internal const int MaxBytes = 500 * 1024;

    internal static string OutputDir =>
        Environment.GetEnvironmentVariable("ORANDA_MANUAL_OUT") is { Length: > 0 } dir
            ? dir
            : Path.Combine(ManualSite.RepoRoot, "site", "src", "assets", "screens");

    /// <summary>창을 밝은 테마로 그려 <paramref name="name"/>.png 로 쓴다. 번호 표시를 주면 컨트롤 위치 위에 얹는다.</summary>
    internal static string Save(Window window, string name, IReadOnlyList<Callout>? callouts = null)
    {
        window.RequestedThemeVariant = ThemeVariant.Light;
        Settle(window);

        Control? original = null;
        if (callouts is { Count: > 0 })
        {
            original = window.Content as Control ?? throw new InvalidOperationException("창 내용이 컨트롤이 아니다.");
            var overlay = WrapWithOverlay(window, original);
            Settle(window);
            foreach (var callout in callouts)
            {
                AddBadge(overlay, callout);
            }

            Settle(window);
        }

        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height)));
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);

        // 번호 표시는 이 캡처에만 쓴다. 같은 창으로 다음 캡처를 이어서 하므로 원래 내용으로 되돌린다.
        if (original is not null)
        {
            ((Panel)window.Content!).Children.Clear();
            window.Content = original;
            Settle(window);
        }

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, name + ".png");
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bitmap.Save(path);
#pragma warning restore CS0618

        var length = new FileInfo(path).Length;
        Assert.True(length <= MaxBytes, $"{name}.png 가 {length / 1024} KB 다(상한 {MaxBytes / 1024} KB). 창 크기를 줄이거나 화면을 나눈다.");
        return path;
    }

    /// <summary>이름이나 조건에 맞는 첫 컨트롤을 시각 트리에서 찾는다. 없으면 시험 실패.</summary>
    internal static T Find<T>(Visual root, Func<T, bool>? match = null)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => match?.Invoke(c) ?? true)
        ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name} 를 찾지 못했다.");

    /// <summary>화면에 보이는 글자(TextBlock 이나 단추 내용)가 <paramref name="text"/> 인 컨트롤.</summary>
    internal static Control FindByText(Visual root, string text)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            var shown = control switch
            {
                TextBlock t => t.Text,
                ContentControl { Content: string s } => s,
                _ => null,
            };
            if (shown == text)
            {
                return control;
            }
        }

        throw new Xunit.Sdk.XunitException($"글자 '{text}' 를 가진 컨트롤을 찾지 못했다.");
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Canvas WrapWithOverlay(Window window, Control original)
    {
        window.Content = null;
        var host = new Grid();
        host.Children.Add(original);
        var overlay = new Canvas { IsHitTestVisible = false };
        host.Children.Add(overlay);
        window.Content = host;
        return overlay;
    }

    private static void AddBadge(Canvas overlay, Callout callout)
    {
        var origin = callout.Target.TranslatePoint(new Point(0, 0), overlay)
            ?? throw new InvalidOperationException($"번호 {callout.Number}: 컨트롤 위치를 알 수 없다.");
        const double diameter = 22;
        var badge = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = new SolidColorBrush(Color.Parse("#D93025")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = callout.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        // 컨트롤의 왼쪽 위 모서리에 반쯤 걸치게 두되 창 밖으로 나가지 않게 한다.
        Canvas.SetLeft(badge, Math.Max(0, origin.X - diameter / 2));
        Canvas.SetTop(badge, Math.Max(0, origin.Y - diameter / 2));
        overlay.Children.Add(badge);
    }
}
