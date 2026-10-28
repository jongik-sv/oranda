using Avalonia.Controls;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 튜닝 시작 확인 창(ai-ui u3): 접속 허용 수준이 끔이면 막고, 운영 접속은 벤치마크를 끈다.</summary>
public class TuningStartTests
{
    private static readonly TuningModelOption[] Models = [new("default", "기본 모델 — Claude Code · opus"), new("local", "ollama 로컬")];

    /// <summary>허용 수준 끔 / 개발·조회 실행(벤치마크 가능) / 운영·계획(벤치마크 불가) 세 모양을 접속 문맥으로 만든다.</summary>
    private static TuningStartInfo Info(bool accessOff = false, bool benchmarkAllowed = true) => Info(
        accessOff ? AiAccessLevel.Off : benchmarkAllowed ? AiAccessLevel.Query : AiAccessLevel.Plan,
        benchmarkAllowed ? ConnectionEnvironment.Development : ConnectionEnvironment.Production);

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
    public void Access_off_blocks_the_start_and_explains_where_to_change_it()
    {
        var vm = new TuningStartViewModel(Info(accessOff: true));
        Assert.True(vm.IsBlocked);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.Choice);
        Assert.Contains("접속 편집", vm.BlockedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_connection_never_benchmarks_even_if_checked()
    {
        var vm = new TuningStartViewModel(Info(benchmarkAllowed: false));
        Assert.False(vm.BenchmarkEnabled);
        Assert.False(vm.Benchmark);
        vm.Benchmark = true;
        Assert.False(vm.Choice!.Benchmark);
    }

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
}
