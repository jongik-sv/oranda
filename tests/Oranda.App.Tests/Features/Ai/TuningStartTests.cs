using Avalonia.Controls;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 튜닝 시작 확인 창(ai-ui u3): 접속 허용 수준이 끔이면 막고, 운영 접속은 벤치마크를 끈다.</summary>
public class TuningStartTests
{
    private static readonly TuningModelOption[] Models = [new("default", "기본 모델 — Claude Code · opus"), new("local", "ollama 로컬")];

    /// <summary>허용 수준 끔 / 개발·조회 실행(벤치마크 가능) / 개발·계획(벤치마크 불가) 세 모양을 접속 문맥으로 만든다.</summary>
    private static TuningStartInfo Info(bool accessOff = false, bool benchmarkAllowed = true) => Info(
        accessOff ? AiAccessLevel.Off : benchmarkAllowed ? AiAccessLevel.Query : AiAccessLevel.Plan,
        ConnectionEnvironment.Development);

    private static TuningStartInfo Info(AiAccessLevel level, ConnectionEnvironment environment) => new(
        "AI 튜닝 — 커서 위치 문장", "8~15행 · SELECT d.dept_id …", "sql_id 7h35uxf5uhmm1 · 실행 1,240회",
        "문장 + 참조 객체", "실제 계획 · 인덱스 · 통계", "행 값 · 바인드 값 · 비밀번호",
        new AiContext("DEV-ERP", environment, level), Models);

    [Fact]
    public void Benchmark_needs_query_level_even_on_a_dev_connection()
    {
        var vm = new TuningStartViewModel(Info(AiAccessLevel.Plan, ConnectionEnvironment.Development));
        Assert.False(vm.BenchmarkEnabled);
        Assert.Equal(TuningStartViewModel.BenchmarkLevelText, vm.BenchmarkNote);
        Assert.False(vm.Choice!.Benchmark);
    }

    [Fact]
    public void Start_returns_the_checked_options_and_the_first_model_by_default()
    {
        var vm = new TuningStartViewModel(Info());
        Assert.Equal(new TuningStartChoice(true, true, "default"), vm.Choice);

        vm.Benchmark = false;
        vm.Model = Models[1];
        Assert.Equal(new TuningStartChoice(false, true, "local"), vm.Choice);
    }

    [Fact]
    public void Run_risky_functions_is_off_by_default_and_only_counts_with_the_benchmark_on()
    {
        var vm = new TuningStartViewModel(Info());
        Assert.False(vm.RunRiskyFunctions);
        Assert.False(vm.Choice!.RunRiskyFunctions);

        vm.RunRiskyFunctions = true;
        Assert.True(vm.Choice!.RunRiskyFunctions);

        vm.Benchmark = false;
        Assert.False(vm.Choice!.RunRiskyFunctions); // 벤치마크를 끄면 뜻이 없다
    }

    [Theory]
    [InlineData("sonnet", "sonnet")]
    [InlineData("haiku", "haiku")]
    [InlineData("opus", "opus")]
    public void Model_rows_are_the_three_known_models_and_the_setting_is_preselected(string setting, string expected)
    {
        var info = AiTuningLauncher.BuildStartInfo(null, new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings { Model = setting });
        Assert.Equal(["opus", "sonnet", "haiku"], info.Models.Select(m => m.Id));

        var vm = new TuningStartViewModel(info);
        Assert.Equal(expected, vm.Model!.Id);
        Assert.Equal(expected, vm.Choice!.ModelId);
    }

    [Fact]
    public void A_setting_outside_the_list_is_added_as_a_row_and_preselected()
    {
        var info = AiTuningLauncher.BuildStartInfo(null, new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings { Model = "claude-opus-4-1" });
        Assert.Equal(["opus", "sonnet", "haiku", "claude-opus-4-1"], info.Models.Select(m => m.Id));
        Assert.Equal("claude-opus-4-1", new TuningStartViewModel(info).Choice!.ModelId);
    }

    [Fact]
    public void An_empty_setting_keeps_the_default_row_that_sends_no_model()
    {
        var info = AiTuningLauncher.BuildStartInfo(null, new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings { Model = " " });
        Assert.Equal(["default", "opus", "sonnet", "haiku"], info.Models.Select(m => m.Id));
        Assert.Equal("default", new TuningStartViewModel(info).Choice!.ModelId);
    }

    [Fact]
    public void Access_off_blocks_the_start_and_explains_where_to_change_it()
    {
        var vm = new TuningStartViewModel(Info(accessOff: true));
        Assert.True(vm.IsBlocked);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.Choice);
        Assert.Contains("접속 편집", vm.BlockedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_connection_at_query_level_may_benchmark_but_starts_with_it_off_and_shows_the_notice()
    {
        var vm = new TuningStartViewModel(Info(AiAccessLevel.Query, ConnectionEnvironment.Production));
        Assert.True(vm.BenchmarkEnabled);
        Assert.False(vm.Benchmark); // 운영은 허용만 하고 기본은 끔
        Assert.Null(vm.BenchmarkNote);
        Assert.Equal(TuningStartViewModel.ProductionNoticeText, vm.ProductionNotice);
        Assert.False(vm.Choice!.Benchmark);

        vm.Benchmark = true;
        Assert.True(vm.Choice!.Benchmark);
    }

    [Fact]
    public void Production_connection_below_query_level_does_not_benchmark_even_if_checked()
    {
        var vm = new TuningStartViewModel(Info(AiAccessLevel.Plan, ConnectionEnvironment.Production));
        Assert.False(vm.BenchmarkEnabled);
        Assert.False(vm.Benchmark);
        vm.Benchmark = true;
        Assert.False(vm.Choice!.Benchmark);
        Assert.Equal(TuningStartViewModel.BenchmarkLevelText, vm.BenchmarkNote);
    }

    [Fact]
    public void Mask_line_follows_the_masking_decision_the_launcher_passes()
    {
        var context = new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);
        Assert.False(new TuningStartViewModel(AiTuningLauncher.BuildStartInfo(null, context, new AiSettings())).MasksLiterals);
        Assert.True(new TuningStartViewModel(AiTuningLauncher.BuildStartInfo(null, context, new AiSettings(), maskLiterals: true)).MasksLiterals);
        Assert.Null(new TuningStartViewModel(AiTuningLauncher.BuildStartInfo(null, context, new AiSettings())).ProductionNotice);
    }

    [Fact]
    public Task Dialog_shows_the_mask_line_and_production_notice_only_when_they_apply() => HeadlessHost.Run(() =>
    {
        var info = Info(AiAccessLevel.Query, ConnectionEnvironment.Production) with { MaskLiterals = true };
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(info) };
        dialog.Show();
        Assert.True(dialog.FindControl<TextBlock>("MaskLine")!.IsVisible);
        Assert.True(dialog.FindControl<Border>("ProductionBanner")!.IsVisible);
        Assert.True(dialog.FindControl<CheckBox>("BenchmarkCheck")!.IsEnabled);
        dialog.Close();

        var plain = new TuningStartDialog { DataContext = new TuningStartViewModel(Info()) };
        plain.Show();
        Assert.False(plain.FindControl<TextBlock>("MaskLine")!.IsVisible);
        Assert.False(plain.FindControl<Border>("ProductionBanner")!.IsVisible);
        plain.Close();
    });

    [Fact]
    public Task Dialog_shows_the_three_send_rows_and_disables_start_when_blocked() => HeadlessHost.Run(() =>
    {
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(Info(accessOff: true)) };
        dialog.Show();

        Assert.Equal("문장 + 참조 객체", dialog.FindControl<TextBlock>("SendFirstText")!.Text);
        Assert.Equal("실제 계획 · 인덱스 · 통계", dialog.FindControl<TextBlock>("SendOnRequestText")!.Text);
        Assert.Equal("행 값 · 바인드 값 · 비밀번호", dialog.FindControl<TextBlock>("NotSentText")!.Text);
        Assert.True(dialog.FindControl<Border>("BlockedBanner")!.IsVisible);
        Assert.False(dialog.FindControl<Button>("StartButton")!.Command!.CanExecute(null));
        dialog.Close();
    });
    [Fact]
    public void Effort_rows_start_with_the_cli_default_when_the_setting_is_empty()
    {
        var context = new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);
        var info = AiTuningLauncher.BuildStartInfo(null, context, new AiSettings());
        Assert.Equal(["default", "low", "medium", "high", "xhigh", "max"], info.Efforts!.Select(e => e.Id));
        var vm = new TuningStartViewModel(info);
        Assert.Equal("default", vm.Choice!.EffortId);

        vm.Effort = vm.Efforts.Single(e => e.Id == "max");
        Assert.Equal("max", vm.Choice!.EffortId);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData(" XHIGH ", "xhigh")]
    public void A_set_effort_is_preselected_and_the_default_row_is_left_out(string setting, string expected)
    {
        var context = new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);
        var info = AiTuningLauncher.BuildStartInfo(null, context, new AiSettings { Effort = setting });
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], info.Efforts!.Select(e => e.Id));
        Assert.Equal(expected, new TuningStartViewModel(info).Choice!.EffortId);
    }

    [Fact]
    public void An_unknown_effort_setting_falls_back_to_the_cli_default() =>
        Assert.Equal("default", new TuningStartViewModel(AiTuningLauncher.BuildStartInfo(null, new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings { Effort = "ultra" })).Choice!.EffortId);

    [Fact]
    public Task Dialog_has_an_effort_box_next_to_the_model() => HeadlessHost.Run(() =>
    {
        var info = AiTuningLauncher.BuildStartInfo(null, new AiContext("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings { Effort = "high" });
        var dialog = new TuningStartDialog { DataContext = new TuningStartViewModel(info) };
        dialog.Show();
        var effort = dialog.FindControl<ComboBox>("EffortCombo")!;
        Assert.Equal(5, effort.ItemCount);
        Assert.Equal("high", ((TuningEffortOption)effort.SelectedItem!).Id);
        var model = dialog.FindControl<ComboBox>("ModelCombo")!;
        Assert.Same(model.Parent, effort.Parent); // 같은 줄(「모델」 옆)

        if (Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            dialog.UpdateLayout();
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)Math.Ceiling(dialog.Bounds.Width), (int)Math.Ceiling(dialog.Bounds.Height)));
            bmp.Render(dialog);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, "ai-tuning-start-effort.png"));
#pragma warning restore CS0618
        }

        dialog.Close();
    });
}
