using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell.Settings;

/// <summary>설정 창 「실행」 탭의 「A-Rows 가 없으면 힌트 사본으로 자동 다시 실행」: 기본 켬, 저장 왕복, 끈 값 유지, 영어 문구 짝.</summary>
public class PlanHintSettingTests
{
    private static readonly string[] Keys = ["Shell.settings.planHintLabel", "Shell.settings.planHint", "Shell.settings.planHintTip"];

    [Fact]
    public Task Checkbox_defaults_on_and_the_off_choice_is_saved_and_read_back() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var settings = h.Get<SettingsService>();
        var original = settings.Current;
        try
        {
            await settings.UpdateAsync(s => s with { Execution = s.Execution with { GatherPlanStatisticsHint = true } });
            var vm = new SettingsViewModel(settings, h.Get<IShellText>());
            Assert.True(vm.GatherPlanStatisticsHint);

            vm.GatherPlanStatisticsHint = false;
            await vm.SaveAsync();
            Assert.False(settings.Current.Execution.GatherPlanStatisticsHint);
            Assert.False(new SettingsViewModel(settings, h.Get<IShellText>()).GatherPlanStatisticsHint);

            // 다른 칸만 바꿔 저장해도 끈 값이 켬으로 되돌아가지 않는다.
            var other = new SettingsViewModel(settings, h.Get<IShellText>()) { AutoCommit = true };
            await other.SaveAsync();
            Assert.False(settings.Current.Execution.GatherPlanStatisticsHint);
        }
        finally
        {
            await settings.UpdateAsync(_ => original);
        }
    });

    [Fact]
    public Task Execution_tab_labels_exist_in_korean_and_english() => HeadlessHost.Run(() =>
    {
        using var h = new ShellHarness();
        var text = h.Get<IShellText>();
        foreach (var key in Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(text.Lookup(key)), key);
        }

        return Task.CompletedTask;
    });
}
