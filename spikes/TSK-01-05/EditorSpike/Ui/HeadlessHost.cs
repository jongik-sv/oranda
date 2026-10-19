using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AvaloniaEdit;

namespace EditorSpike.Ui;

public sealed class SpikeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://EditorSpike/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
    }
}

/// <summary>
/// 헤드리스 Avalonia(Skia 실제 래스터화, UseHeadlessDrawing=false). 호출한 스레드가 UI 스레드가 된다.
/// </summary>
public static class HeadlessHost
{
    static bool _started;

    public static void Start()
    {
        if (_started) return;
        AppBuilder.Configure<SpikeApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        _started = true;
    }

    public static (Window window, TextEditor editor) CreateEditorWindow(string text, int width = 1600, int height = 1000)
    {
        var editor = new TextEditor
        {
            FontFamily = new Avalonia.Media.FontFamily("Consolas, Cascadia Mono, Courier New, monospace"),
            FontSize = 14,
            ShowLineNumbers = true,
            Text = text,
        };
        var window = new Window { Width = width, Height = height, Content = editor };
        window.Show();
        Pump();
        editor.Focus();
        editor.TextArea.Focus();
        Pump();
        // 첫 프레임
        Frame(window);
        return (window, editor);
    }

    public static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>레이아웃·렌더를 강제하고 화면 프레임을 실제로 래스터화한다.</summary>
    public static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var bmp = window.CaptureRenderedFrame();
        if (bmp is null) throw new InvalidOperationException("프레임 캡처 실패");
    }
}
