using Avalonia.Controls;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>설정 AI 모델 쪽과 접속 편집 AI 쪽(ai-ui u6): 규칙은 Core 의 AiAccessPolicy 를 따른다(운영은 계획까지, 켜려면 이름 재입력).</summary>
public class AiSettingsTests
{
    private static ConnectionProfile Profile(ConnectionEnvironment environment, AiAccessLevel? level = null, bool? explain = null) =>
        new("ERP", "APP_OWNER", "db-example", Environment: environment, AiAccess: level, AllowExplainPlan: explain);

    [Fact]
    public void Model_page_enables_the_model_list_only_for_claude_code_and_keeps_untouched_settings()
    {
        var page = new AiModelSettingsPage(new AiSettings { ExecutablePath = "claude-example", MaskLiterals = false });
        Assert.False(page.ModelEnabled);
        Assert.Equal("AI: 선택 안 함", page.Summary);
        Assert.Null(page.ToSettings().Model);

        page.IsClaudeCode = true;
        page.Model = "sonnet";
        Assert.True(page.ModelEnabled);
        Assert.Equal("AI: Claude Code · sonnet", page.Summary);
        var saved = page.ToSettings();
        Assert.Equal((AiDriverKind.ClaudeCode, "sonnet", "claude-example"), (saved.Driver, saved.Model, saved.ExecutablePath));
    }

    [Fact]
    public void Dev_connection_shows_the_environment_default_and_reaches_query_rows_without_confirmation()
    {
        var vm = new ConnectionAiAccessViewModel(Profile(ConnectionEnvironment.Development));
        Assert.Equal(AiAccessLevel.Plan, vm.Level); // 개발 기본
        Assert.True(vm.ExplainAllowed);

        vm.SelectLevelCommand.Execute(AiAccessLevel.QueryRows);
        Assert.Equal(AiAccessLevel.QueryRows, vm.Level);
        Assert.Single(vm.Levels, l => l.IsSelected);
        Assert.False(vm.NeedsNameConfirmation);
        Assert.True(vm.CanSave);
        Assert.Equal(AiAccessLevel.QueryRows, vm.Result.AiAccess);
    }

    [Fact]
    public void Production_defaults_to_off_caps_at_plan_and_asks_for_the_name_when_turned_on()
    {
        var vm = new ConnectionAiAccessViewModel(Profile(ConnectionEnvironment.Production));
        Assert.Equal(AiAccessLevel.Off, vm.Level);
        Assert.False(vm.ExplainAllowed);
        Assert.All(vm.Levels.Where(l => l.Level > AiAccessLevel.Plan), l => Assert.False(l.IsEnabled));

        vm.SelectLevelCommand.Execute(AiAccessLevel.Query);
        Assert.Equal(AiAccessLevel.Off, vm.Level); // 상한 밖은 무시

        vm.SelectLevelCommand.Execute(AiAccessLevel.Plan);
        Assert.True(vm.NeedsNameConfirmation);
        Assert.False(vm.CanSave);
        vm.ConfirmName = "ERP";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Switching_a_dev_connection_to_production_clamps_and_asks_for_the_name()
    {
        var vm = new ConnectionAiAccessViewModel(Profile(ConnectionEnvironment.Development, AiAccessLevel.QueryRows));
        vm.Environment = ConnectionEnvironment.Production;

        Assert.Equal(AiAccessLevel.Plan, vm.Level);
        Assert.True(vm.NeedsNameConfirmation);
        Assert.Equal(AiAccessLevel.Plan, vm.Result.AiAccess);
    }

    [Fact]
    public Task Access_view_shows_five_levels_and_the_name_box_only_when_the_name_is_needed() => HeadlessHost.Run(() =>
    {
        var vm = new ConnectionAiAccessViewModel(Profile(ConnectionEnvironment.Production));
        var window = new Window { Width = 760, Content = new ConnectionAiAccessView { DataContext = vm } };
        window.Show();

        var view = (ConnectionAiAccessView)window.Content!;
        Assert.Equal(5, view.FindControl<ItemsControl>("LevelList")!.ItemCount);
        Assert.False(view.FindControl<Grid>("ConfirmRow")!.IsVisible);
        Assert.Equal("—", view.FindControl<TextBlock>("ToolsText")!.Text);

        vm.SelectLevelCommand.Execute(AiAccessLevel.Plan);
        Assert.True(view.FindControl<Grid>("ConfirmRow")!.IsVisible);
        window.Close();
    });
}
