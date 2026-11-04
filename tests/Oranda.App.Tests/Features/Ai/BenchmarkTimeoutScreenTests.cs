using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// 운영 벤치마크 제한 시간(시작 창 입력 칸 · AI 설정 칸): 표시 조건, 이번 실행 값이 설정에 저장되지 않음, 0 의 경고, 잘못된 입력 막기,
/// 이어 말하기가 시작 값을 유지함. 렌더 PNG 는 <c>ORANDA_RENDER_DIR</c> 가 있을 때만 저장한다.
/// </summary>
public class BenchmarkTimeoutScreenTests
{
    private static readonly TuningModelOption[] Models = [new("default", "기본 모델 — Claude Code · opus")];

    private static TuningStartInfo Info(ConnectionEnvironment environment, AiAccessLevel level = AiAccessLevel.Query, int seconds = 30) => new(
        "AI 튜닝 — 커서 위치 문장", "8~15행 · SELECT d.dept_id …", null,
        "문장 + 참조 객체", "실제 계획 · 인덱스 · 통계", "행 값 · 바인드 값(로컬 실행에만 씀) · 비밀번호",
        new AiContext("ERP-PROD", environment, level), Models, BenchmarkTimeoutSeconds: seconds);

    private static TuningStartViewModel Production(int seconds = 30, bool benchmark = true) =>
        new(Info(ConnectionEnvironment.Production, seconds: seconds)) { Benchmark = benchmark };

    private static void Render(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
        bmp.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    private static bool Inside(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window);
        return origin is { } p && p.X >= -0.5 && p.Y >= -0.5 && p.X + control.Bounds.Width <= window.Bounds.Width + 0.5 && p.Y + control.Bounds.Height <= window.Bounds.Height + 0.5;
    }

    // ── 시작 창: 표시 조건 ──

    [Fact]
    public void The_input_shows_only_on_a_production_connection_with_the_benchmark_on()
    {
        Assert.True(Production().ShowBenchmarkTimeout);
        Assert.False(Production(benchmark: false).ShowBenchmarkTimeout); // 벤치마크를 끄면 숨김
        Assert.False(new TuningStartViewModel(Info(ConnectionEnvironment.Production, AiAccessLevel.Plan)) { Benchmark = true }.ShowBenchmarkTimeout); // 허용 수준 미달
        Assert.False(new TuningStartViewModel(Info(ConnectionEnvironment.Development)) { Benchmark = true }.ShowBenchmarkTimeout); // 개발은 제한이 없다
        Assert.False(new TuningStartViewModel(Info(ConnectionEnvironment.Test)) { Benchmark = true }.ShowBenchmarkTimeout);
    }

    [Fact]
    public void Toggling_the_benchmark_notifies_the_input_visibility()
    {
        var vm = Production(benchmark: false);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Benchmark = true;

        Assert.Contains(nameof(TuningStartViewModel.ShowBenchmarkTimeout), changed);
    }

    // ── 시작 창: 처음 값과 이번 실행 값 ──

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(0)]
    public void The_first_value_is_the_setting_default_and_goes_into_the_choice(int configured)
    {
        var vm = Production(configured);

        Assert.Equal(configured.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.BenchmarkTimeoutText);
        Assert.Equal(configured, vm.Choice!.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void Launcher_start_info_carries_the_setting_default()
    {
        var context = new AiContext("ERP-PROD", ConnectionEnvironment.Production, AiAccessLevel.Query);

        Assert.Equal(30, Oranda.App.Features.Ai.Tuning.AiTuningLauncher.BuildStartInfo(null, context, new AiSettings()).BenchmarkTimeoutSeconds);
        Assert.Equal(90, Oranda.App.Features.Ai.Tuning.AiTuningLauncher.BuildStartInfo(null, context, new AiSettings { BenchmarkTimeoutSeconds = 90 }).BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void A_value_typed_for_this_run_reaches_the_choice_but_never_the_settings()
    {
        var settings = new AiSettings { BenchmarkTimeoutSeconds = 60 };
        var info = Oranda.App.Features.Ai.Tuning.AiTuningLauncher.BuildStartInfo(null, new AiContext("ERP-PROD", ConnectionEnvironment.Production, AiAccessLevel.Query), settings);
        var vm = new TuningStartViewModel(info) { Benchmark = true };

        vm.BenchmarkTimeoutText = "300";

        Assert.Equal(300, vm.Choice!.BenchmarkTimeoutSeconds);
        Assert.Equal(60, settings.BenchmarkTimeoutSeconds); // 설정 사본은 그대로: 시작 창은 설정을 쓰지 않는다
        Assert.Equal(60, info.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void The_start_dialog_does_not_write_the_settings_service()
    {
        // 시작 창 쪽 코드는 설정 저장소를 받지 않는다: 값은 선택(Choice)으로만 나간다.
        var ctor = typeof(TuningStartViewModel).GetConstructors().Single();
        Assert.Equal([typeof(TuningStartInfo)], ctor.GetParameters().Select(p => p.ParameterType));
    }

    // ── 시작 창: 0 의 경고와 잘못된 입력 ──

    [Fact]
    public void Zero_shows_the_warning_and_keeps_start_enabled()
    {
        var vm = Production();
        Assert.False(vm.ShowBenchmarkTimeoutWarning);

        vm.BenchmarkTimeoutText = "0";

        Assert.True(vm.ShowBenchmarkTimeoutWarning);
        Assert.Equal("사용자가 중지할 때까지 운영 DB 에서 실행합니다", TuningStartViewModel.TimeoutUnlimitedWarningText);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(0, vm.Choice!.BenchmarkTimeoutSeconds); // 제한 없음은 명시했을 때만 나온다
    }

    [Theory]
    [InlineData("4")]
    [InlineData("1")]
    [InlineData("3601")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    public void An_invalid_value_blocks_the_start_and_never_turns_into_unlimited(string text)
    {
        var vm = Production();

        vm.BenchmarkTimeoutText = text;

        Assert.False(vm.BenchmarkTimeoutValid);
        Assert.True(vm.ShowBenchmarkTimeoutError);
        Assert.False(vm.ShowBenchmarkTimeoutWarning);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.Choice);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData(" 45 ", 45)]
    [InlineData("3600", 3600)]
    public void Valid_values_are_accepted(string text, int expected)
    {
        var vm = Production();

        vm.BenchmarkTimeoutText = text;

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal(expected, vm.Choice!.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void A_bad_value_does_not_block_a_start_whose_input_is_hidden()
    {
        var vm = Production(benchmark: false);
        vm.BenchmarkTimeoutText = "abc";

        Assert.True(vm.StartCommand.CanExecute(null)); // 칸이 안 보이면 값을 쓰지 않는다
        Assert.Equal(30, vm.Choice!.BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void A_hidden_input_on_production_never_applies_a_saved_zero_without_the_warning()
    {
        var hidden = Production(0, benchmark: false);
        Assert.Equal(30, hidden.Choice!.BenchmarkTimeoutSeconds); // 경고를 보지 않은 0 은 30초로 닫는다

        hidden.Benchmark = true; // 칸이 보이면 0 과 경고가 함께 나온다
        Assert.Equal(0, hidden.Choice!.BenchmarkTimeoutSeconds);
        Assert.True(hidden.ShowBenchmarkTimeoutWarning);

        Assert.Equal(120, Production(120, benchmark: false).Choice!.BenchmarkTimeoutSeconds);
    }

    // ── 이어 말하기: 시작 값 유지 ──

    private sealed class Services(SettingsService? settings = null) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(SettingsService) ? settings : null;
    }

    [Fact]
    public void Follow_up_sessions_keep_the_value_confirmed_at_the_start()
    {
        var launcher = new AiTuningLauncher(new Services())
        {
            SessionTracker = session => session,
            ActiveProfile = new ConnectionProfile("ERP-PROD", "scott", "h/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production),
        };
        var context = new AiContext("ERP-PROD", ConnectionEnvironment.Production, AiAccessLevel.Query);

        Assert.Equal(30, launcher.NewSessionFor(general: false, context, null)!.Run.BenchmarkTimeoutSeconds); // 시작 전 기본은 30초

        launcher.CaptureBenchmarkTimeout(0);

        Assert.Equal(0, launcher.NewSessionFor(general: false, context, null)!.Run.BenchmarkTimeoutSeconds);
        Assert.Equal(0, launcher.NewSessionFor(general: false, context, null)!.Run.BenchmarkTimeoutSeconds); // 이어 말하기마다 새 범위를 만들어도 같은 값
        Assert.Equal(0, launcher.StartedBenchmarkTimeoutSeconds);
    }

    [Fact]
    public void A_rejected_start_restores_the_previous_conversation_value_and_out_of_range_closes_to_30()
    {
        var launcher = new AiTuningLauncher(new Services());
        launcher.CaptureBenchmarkTimeout(120);

        var previous = launcher.CaptureBenchmarkTimeout(0);
        Assert.Equal(120, previous);

        launcher.CaptureBenchmarkTimeout(previous); // 거부된 시작을 되돌린다
        Assert.Equal(120, launcher.StartedBenchmarkTimeoutSeconds);

        launcher.CaptureBenchmarkTimeout(3);
        Assert.Equal(30, launcher.StartedBenchmarkTimeoutSeconds); // 범위 밖은 제한 없음이 아니라 30
    }

    [Fact]
    public void General_chat_sessions_use_the_settings_default_not_the_tuning_value()
    {
        var launcher = new AiTuningLauncher(new Services())
        {
            SessionTracker = session => session,
            ActiveProfile = new ConnectionProfile("ERP-PROD", "scott", "h/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production),
        };
        launcher.CaptureBenchmarkTimeout(0); // 앞 튜닝에서 이번 실행만 제한 없음으로 정했다

        var general = launcher.NewSessionFor(general: true, new AiContext("ERP-PROD", ConnectionEnvironment.Production, AiAccessLevel.Query), null)!;

        Assert.Equal(30, general.Run.BenchmarkTimeoutSeconds); // 설정을 못 읽으면 닫는 값 30. 앞 튜닝의 0 이 새지 않는다
    }

    [Theory]
    [InlineData(0, 30)] // 저장된 0 도 일반 대화에서는 30초로 닫는다
    [InlineData(45, 45)]
    [InlineData(5, 5)]
    [InlineData(3600, 3600)]
    [InlineData(30, 30)]
    public async Task General_chat_reads_the_saved_setting_but_closes_zero_to_30(int saved, int expected)
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(store);
        await settings.UpdateAsync(s => s with { Ai = s.Ai with { BenchmarkTimeoutSeconds = saved } });
        var launcher = new AiTuningLauncher(new Services(settings))
        {
            SessionTracker = session => session,
            ActiveProfile = new ConnectionProfile("ERP-PROD", "scott", "h/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production),
        };
        launcher.CaptureBenchmarkTimeout(120); // 앞 튜닝의 값도 섞이지 않는다
        var context = new AiContext("ERP-PROD", ConnectionEnvironment.Production, AiAccessLevel.Query);

        Assert.Equal(expected, launcher.NewSessionFor(general: true, context, null)!.Run.BenchmarkTimeoutSeconds);
        Assert.Equal(120, launcher.NewSessionFor(general: false, context, null)!.Run.BenchmarkTimeoutSeconds); // 튜닝 쪽은 시작 값
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 30)]
    [InlineData(4, 30)]
    [InlineData(60, 60)]
    public void General_chat_helper_closes_missing_zero_and_out_of_range(int? configured, int expected) =>
        Assert.Equal(expected, AiTuningLauncher.GeneralChatBenchmarkTimeout(configured));

    // ── AI 설정 칸 ──

    [Fact]
    public void The_settings_page_reads_the_saved_value_and_writes_a_valid_edit()
    {
        var page = new AiModelSettingsPage(new AiSettings { BenchmarkTimeoutSeconds = 90 });
        Assert.Equal("90", page.BenchmarkTimeoutText);
        Assert.Equal(90, page.ToSettings().BenchmarkTimeoutSeconds);

        page.BenchmarkTimeoutText = "600";

        Assert.Equal(600, page.ToSettings().BenchmarkTimeoutSeconds);
        Assert.Equal(600, page.ToSettings(new AiSettings { BenchmarkTimeoutSeconds = 45 }).BenchmarkTimeoutSeconds);
    }

    [Fact]
    public void The_settings_page_keeps_the_previous_value_when_the_text_is_invalid_and_shows_the_zero_warning()
    {
        var page = new AiModelSettingsPage(new AiSettings { BenchmarkTimeoutSeconds = 90 });

        page.BenchmarkTimeoutText = "abc";
        Assert.True(page.ShowBenchmarkTimeoutError);
        Assert.Equal(90, page.ToSettings().BenchmarkTimeoutSeconds); // 깨진 글이 제한 없음으로 저장되지 않는다

        page.BenchmarkTimeoutText = "4";
        Assert.Equal(90, page.ToSettings().BenchmarkTimeoutSeconds);

        page.BenchmarkTimeoutText = "0";
        Assert.False(page.ShowBenchmarkTimeoutError);
        Assert.True(page.ShowBenchmarkTimeoutWarning);
        Assert.Equal(0, page.ToSettings().BenchmarkTimeoutSeconds);
    }

    // ── 렌더 점검 ──

    [Fact]
    public Task Start_dialog_renders_the_input_for_production_with_the_benchmark_on() => HeadlessHost.Run(() =>
    {
        var dialog = new TuningStartDialog { DataContext = Production() };
        dialog.Show();

        var panel = dialog.FindControl<StackPanel>("BenchmarkTimeoutPanel")!;
        Assert.True(panel.IsVisible);
        Assert.Equal("30", dialog.FindControl<TextBox>("BenchmarkTimeoutBox")!.Text);
        Assert.False(dialog.FindControl<TextBlock>("BenchmarkTimeoutError")!.IsVisible);
        Assert.False(dialog.FindControl<Border>("BenchmarkTimeoutWarning")!.IsVisible);
        Assert.True(Inside(panel, dialog));
        Assert.True(Inside(dialog.FindControl<Button>("StartButton")!, dialog));
        Render(dialog, "ai-start-timeout-input");
        dialog.Close();
    });

    [Fact]
    public Task Start_dialog_renders_the_warning_for_zero_and_the_error_for_a_bad_value() => HeadlessHost.Run(() =>
    {
        var vm = Production(0);
        var dialog = new TuningStartDialog { DataContext = vm };
        dialog.Show();

        Assert.True(dialog.FindControl<Border>("BenchmarkTimeoutWarning")!.IsVisible);
        Assert.Equal(TuningStartViewModel.TimeoutUnlimitedWarningText, ((TextBlock)dialog.FindControl<Border>("BenchmarkTimeoutWarning")!.Child!).Text);
        Assert.True(Inside(dialog.FindControl<Border>("BenchmarkTimeoutWarning")!, dialog));
        Assert.True(Inside(dialog.FindControl<Button>("StartButton")!, dialog));
        Render(dialog, "ai-start-timeout-zero-warning");

        vm.BenchmarkTimeoutText = "2";
        Assert.False(dialog.FindControl<Border>("BenchmarkTimeoutWarning")!.IsVisible);
        Assert.True(dialog.FindControl<TextBlock>("BenchmarkTimeoutError")!.IsVisible);
        Assert.False(dialog.FindControl<Button>("StartButton")!.Command!.CanExecute(null));
        Render(dialog, "ai-start-timeout-error");
        dialog.Close();
    });

    [Fact]
    public Task Start_dialog_hides_the_input_when_the_benchmark_is_off_or_the_connection_is_not_production() => HeadlessHost.Run(() =>
    {
        var off = new TuningStartDialog { DataContext = Production(benchmark: false) };
        off.Show();
        Assert.False(off.FindControl<StackPanel>("BenchmarkTimeoutPanel")!.IsVisible);
        Render(off, "ai-start-timeout-hidden-benchmark-off");
        off.Close();

        var dev = new TuningStartDialog { DataContext = new TuningStartViewModel(Info(ConnectionEnvironment.Development)) { Benchmark = true } };
        dev.Show();
        Assert.False(dev.FindControl<StackPanel>("BenchmarkTimeoutPanel")!.IsVisible);
        Render(dev, "ai-start-timeout-hidden-development");
        dev.Close();

        // 같은 창에서 벤치마크 확인란을 켜면 칸이 나타난다.
        var vm = Production(benchmark: false);
        var toggled = new TuningStartDialog { DataContext = vm };
        toggled.Show();
        Assert.False(toggled.FindControl<StackPanel>("BenchmarkTimeoutPanel")!.IsVisible);
        vm.Benchmark = true;
        Assert.True(toggled.FindControl<StackPanel>("BenchmarkTimeoutPanel")!.IsVisible);
        toggled.Close();
    });

    [Fact]
    public Task Settings_view_renders_the_default_field_inside_the_window() => HeadlessHost.Run(() =>
    {
        var page = new AiModelSettingsPage(new AiSettings { Driver = AiDriverKind.ClaudeCode, Model = "sonnet", BenchmarkTimeoutSeconds = 45 });
        var view = new AiModelSettingsView { DataContext = page };
        var window = new Window { Width = 640, Height = 560, Content = view };
        window.Show();

        var box = view.FindControl<TextBox>("BenchmarkTimeoutBox")!;
        Assert.Equal("45", box.Text);
        Assert.True(Inside(box, window));
        Assert.True(view.FindControl<RadioButton>("JoinSyntaxOracle")!.IsChecked);
        Assert.False(view.FindControl<RadioButton>("JoinSyntaxAnsi")!.IsChecked);
        Assert.True(Inside(view.FindControl<RadioButton>("JoinSyntaxAnsi")!, window));
        Assert.False(view.FindControl<TextBlock>("BenchmarkTimeoutError")!.IsVisible);
        Assert.False(view.FindControl<StackPanel>("BenchmarkTimeoutWarning")!.IsVisible);
        Render(window, "ai-settings-timeout");

        page.BenchmarkTimeoutText = "0";
        Assert.True(view.FindControl<StackPanel>("BenchmarkTimeoutWarning")!.IsVisible);
        Assert.True(Inside(view.FindControl<StackPanel>("BenchmarkTimeoutWarning")!, window));
        Render(window, "ai-settings-timeout-zero");
        window.Close();
    });
}
