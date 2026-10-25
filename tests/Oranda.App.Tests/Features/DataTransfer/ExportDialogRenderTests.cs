using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Localization;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Grid;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Features.DataTransfer;

/// <summary>
/// TSK-09-07 하나로 합친 내보내기 대화상자 화면(Headless): 상태마다(Excel 빠른 길·거르기·선택·경로 오류·INSERT·내보내는 중)
/// ko/en 에서 글자가 잘리지 않고(기본·최소 폭), 형식에 따라 칸이 보이고 숨고, 키보드만으로 모든 칸에 닿는다.
/// <c>ORANDA_RENDER_DIR</c> 이 있으면 라이트·다크 PNG(<c>export-*.png</c>)를 남긴다.
/// </summary>
[Collection("UiLanguage")]
public sealed partial class ExportDialogRenderTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;
    private readonly string? _oldFolder = ExcelExportPlan.LastFolder;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-export-ui-" + Guid.NewGuid().ToString("N"));

    public ExportDialogRenderTests()
    {
        Directory.CreateDirectory(_dir);
        ExcelExportPlan.LastFolder = _dir;
    }

    public void Dispose()
    {
        UiText.Current = _previousText;
        ExcelExportPlan.LastFolder = _oldFolder;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static readonly string[] TabStops = ["FormatBox", "PathBox", "BrowseButton", "OpenAfterBox", "HeaderBox", "SheetNameBox", "StartButton", "CloseButton"];

    [GeneratedRegex("[가-힣]")]
    private static partial Regex Hangul();

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>폭을 바꾼 뒤 고급 칸 펼침 전환(애니메이션)까지 끝나게 렌더 타이머를 넉넉히 돌린다.</summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(20);
            Pump();
            window.UpdateLayout();
        }
    }

    private static void UseLanguage(string lang)
    {
        var text = new ShellText(language: lang);
        UiText.Current = text;
        LiteralTranslator.Install(text);
    }

    private static ExportDialogWindow Show(ExportDialogViewModel vm, bool expandAdvanced = false)
    {
        var window = new ExportDialogWindow { DataContext = vm };

        // 펼침 애니메이션이 진행 중인 배치를 재지 않도록 띄우기 전에 펼친다.
        Named<Expander>(window, "AdvancedExpander").IsExpanded = expandAdvanced;

        window.Show();
        LiteralTranslator.Translate(window);
        Settle(window);
        return window;
    }

    private static T Named<T>(Window w, string name)
        where T : Control => w.FindControl<T>(name) ?? throw new InvalidOperationException(name);

    private static void Shots(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                var size = new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"export-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    private static void AssertNotClipped(Window window, string state)
    {
        foreach (var width in new[] { window.Width, window.MinWidth })
        {
            window.Width = width;
            Settle(window);

            if (width == window.MinWidth)
            {
                Shots(window, state + "-min");
            }

            var clipped = TextClipping.Find(window);
            Assert.True(clipped.Count == 0, $"{state} 폭 {width}: " + string.Join(" | ", clipped));
        }

        window.Width = 600;
        window.UpdateLayout();
    }

    private static void AssertNoKoreanLeft(Window window)
    {
        Assert.False(Hangul().IsMatch(window.Title ?? string.Empty), window.Title);
        var korean = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text is { } s && Hangul().IsMatch(s))
            .Select(t => t.Text).ToList();
        Assert.True(korean.Count == 0, string.Join(" | ", korean));
    }

    private sealed record State(string Name, Func<ResultGridModel, ExportDialogViewModel> Make, Action<ExportDialogWindow, ExportDialogViewModel>? Check = null)
    {
        public bool ExpandAdvanced { get; init; }
    }

    private State[] States(ResultGridModel grid, ResultGridModel filtered, ExportDialogTests.RecordingTransfer service) =>
    [
        new("excel", g => new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Xlsx, openAfter: true),
            (w, vm) =>
            {
                Assert.True(Named<RadioButton>(w, "AllRowsRadio").IsChecked);
                Assert.True(Named<RadioButton>(w, "LoadedRadio").IsEffectivelyVisible);
                Assert.False(Named<RadioButton>(w, "FetchedRadio").IsEffectivelyVisible);
                Assert.False(Named<RadioButton>(w, "SelectionRadio").IsEnabled);
                Assert.True(Named<Expander>(w, "AdvancedExpander").IsEffectivelyVisible);
                Assert.False(Named<Expander>(w, "AdvancedExpander").IsExpanded); // 고급은 접혀 있다
                Assert.False(Named<TextBox>(w, "InsertTableBox").IsEffectivelyVisible);
                Assert.False(Named<TextBlock>(w, "PathErrorText").IsEffectivelyVisible);
                Assert.False(Named<Button>(w, "CancelButton").IsEffectivelyVisible);
                Assert.True(Named<Button>(w, "StartButton").IsEffectivelyEnabled);
            }),
        new("excel-advanced", g => new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Xlsx, openAfter: true),
            (w, vm) =>
            {
                Assert.True(Named<TextBox>(w, "SheetNameBox").IsEffectivelyVisible);
                Assert.True(Named<NumericUpDown>(w, "MaxRowsBox").IsEffectivelyVisible);
                Assert.True(Named<CheckBox>(w, "HeaderBox").IsEffectivelyVisible);
            }) { ExpandAdvanced = true },
        new("filtered-selection", g => new ExportDialogViewModel(service, null, filtered, TransferFormat.Csv),
            (w, vm) =>
            {
                Assert.False(Named<RadioButton>(w, "AllRowsRadio").IsEnabled);
                Assert.True(Named<RadioButton>(w, "LoadedRadio").IsChecked);
                Assert.True(Named<RadioButton>(w, "FetchedRadio").IsEffectivelyVisible);
                Assert.True(Named<RadioButton>(w, "SelectionRadio").IsEnabled);
            }),
        new("path-error", g => new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Json) { FilePath = Path.Combine(_dir, "missing-folder", "out.json") },
            (w, vm) =>
            {
                Assert.True(Named<TextBlock>(w, "PathErrorText").IsEffectivelyVisible);
                Assert.False(Named<Button>(w, "StartButton").IsEffectivelyEnabled);
                Assert.False(Named<Expander>(w, "AdvancedExpander").IsEffectivelyVisible); // JSON 은 고급 칸이 없다
            }),
        new("insert", g => new ExportDialogViewModel(service, ExportDialogTests.Source("SCOTT.EMP"), grid, TransferFormat.Insert),
            (w, vm) => Assert.True(Named<TextBox>(w, "InsertTableBox").IsEffectivelyVisible)),
    ];

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task Every_state_renders_without_clipping_and_shows_the_right_fields(string lang) => HeadlessHost.Run(async () =>
    {
        UseLanguage(lang);
        try
        {
            await using var grid = await ExportDialogTests.GridAsync(500);
            await using var filtered = await ExportDialogTests.GridAsync(30);
            await filtered.SetLocalFilterAsync(new LocalFilterSpec { QuickText = "n2" });
            filtered.SelectCell(new GridCell(0, 0));
            filtered.ExtendTo(new GridCell(2, 1));
            var service = new ExportDialogTests.RecordingTransfer();
            foreach (var state in States(grid, filtered, service))
            {
                using var vm = state.Make(grid);
                var window = Show(vm, state.ExpandAdvanced);
                try
                {
                    state.Check?.Invoke(window, vm);
                    if (lang == "en")
                    {
                        AssertNoKoreanLeft(window);
                    }

                    Shots(window, $"{state.Name}-{lang}");
                    AssertNotClipped(window, $"{state.Name}-{lang}");
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            UseLanguage("ko");
        }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task Running_state_shows_progress_and_a_stop_button(string lang) => HeadlessHost.Run(async () =>
    {
        UseLanguage(lang);
        try
        {
            await using var grid = await ExportDialogTests.GridAsync(50);
            var service = new ExportDialogTests.RecordingTransfer { Block = true };
            using var vm = new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Xlsx, openAfter: false);
            var window = Show(vm);
            try
            {
                var run = vm.StartAsync();
                await service.Started.Task;
                Pump();
                window.UpdateLayout();
                Assert.True(Named<Button>(window, "CancelButton").IsEffectivelyVisible);
                Assert.False(Named<Button>(window, "StartButton").IsEffectivelyEnabled);
                Assert.True(Named<TextBlock>(window, "StatusText").IsEffectivelyVisible);
                Assert.False(Named<TextBox>(window, "PathBox").IsEffectivelyEnabled); // 내보내는 동안 고른 값은 잠긴다
                Assert.False(Named<ComboBox>(window, "FormatBox").IsEffectivelyEnabled);
                Assert.Empty(TextClipping.Find(window));
                Shots(window, $"running-{lang}");

                Named<Button>(window, "CancelButton").Command!.Execute(null);
                await run;
                Pump();
                Assert.False(Named<Button>(window, "CancelButton").IsEffectivelyVisible);
                Assert.True(Named<Button>(window, "StartButton").IsEffectivelyEnabled);
                Assert.True(Named<TextBox>(window, "PathBox").IsEffectivelyEnabled);
                Assert.True(window.IsVisible); // 취소하면 창은 그대로(다시 시도)
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            UseLanguage("ko");
        }
    });

    [Fact]
    public Task Keyboard_reaches_every_field_and_enter_starts_the_export() => HeadlessHost.Run(async () =>
    {
        await using var grid = await ExportDialogTests.GridAsync(20);
        grid.SelectCell(new GridCell(0, 0));
        var service = new ExportDialogTests.RecordingTransfer();
        var opened = new List<string>();
        var oldOpen = ExportRunner.OpenFile;
        ExportRunner.OpenFile = p => { opened.Add(p); return true; };
        var oldFormat = ExcelExportPlan.LastFormat;
        var oldOpenAfter = ExcelExportPlan.LastOpenAfter;
        try
        {
            using var vm = new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Xlsx, openAfter: true);
            var window = Show(vm, expandAdvanced: true);
            try
            {
                // 첫 포커스는 「내보내기」: 저장 위치가 채워져 있어 Enter 한 번이면 된다.
                Assert.Same(Named<Button>(window, "StartButton"), window.FocusManager?.GetFocusedElement());
                var pathBox = Named<TextBox>(window, "PathBox");
                Assert.Equal(pathBox.Text!.Length, pathBox.CaretIndex); // 긴 경로는 파일 이름 쪽이 보인다

                // Tab 으로 도는 순서에 모든 입력 칸이 있다(고급 칸은 펼친 상태).
                var seen = new List<string>();
                Named<ComboBox>(window, "FormatBox").Focus();
                for (var i = 0; i < 30; i++)
                {
                    if (window.FocusManager?.GetFocusedElement() is Control { Name: { } n } && !seen.Contains(n))
                    {
                        seen.Add(n);
                    }

                    window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                    Pump();
                }

                foreach (var name in TabStops)
                {
                    Assert.Contains(name, seen);
                }

                Assert.True(seen.IndexOf("FormatBox") < seen.IndexOf("PathBox"));
                Assert.True(seen.IndexOf("PathBox") < seen.IndexOf("StartButton"));
                Assert.Contains(seen, n => n.EndsWith("Radio", StringComparison.Ordinal)); // 범위 단추 묶음에 들어간다

                Named<Button>(window, "StartButton").Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await HeadlessHost.WaitUntil(() => !window.IsVisible);
                Assert.Equal(1, service.ServerExports);
                Assert.Single(opened);
                Assert.True(File.Exists(opened[0]));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            ExportRunner.OpenFile = oldOpen;
            ExcelExportPlan.LastFormat = oldFormat;
            ExcelExportPlan.LastOpenAfter = oldOpenAfter;
        }
    });

    [Fact]
    public Task Escape_closes_without_writing() => HeadlessHost.Run(async () =>
    {
        await using var grid = await ExportDialogTests.GridAsync(5);
        var service = new ExportDialogTests.RecordingTransfer();
        using var vm = new ExportDialogViewModel(service, ExportDialogTests.Source(), grid, TransferFormat.Csv, openAfter: false);
        var window = Show(vm);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump();
        Assert.False(window.IsVisible);
        Assert.Equal(0, service.ServerExports);
        Assert.False(File.Exists(vm.FilePath));
    });
}
