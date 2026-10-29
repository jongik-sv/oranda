using Oranda.App.Features.Ai;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Menus;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Update;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Bridge;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 튜닝 명령 배치와 활성 조건, 문맥 키, 설정·접속 편집 끼우기, 다리 시작 조건(ai-ui u2·u6).</summary>
public class AiCommandsTests
{
    private static readonly IReadOnlyList<CommandDefinition> AppCommands =
        [.. ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition)];

    [Fact]
    public void Toolbar_has_one_ai_split_button_whose_children_hold_tune_statement_first()
    {
        var nodes = MenuModelBuilder.BuildToolbar(AppCommands, new ShellText(language: "ko"), _ => null);

        var tune = nodes.Single(n => n.CommandId == AiCommands.Tune);
        Assert.Equal("aiTune", tune.Icon);
        Assert.Equal(
            [AiCommands.TuneStatement, AiCommands.ExplainPlan, AiCommands.ExplainSql, AiCommands.FixError],
            tune.Children.Select(c => c.CommandId!));
        Assert.DoesNotContain(nodes, n => n.CommandId == AiCommands.TuneStatement);
    }

    [Fact]
    public void Tune_explains_what_is_missing_and_unfinished_children_never_enable()
    {
        var keys = new ContextKeyService();
        var text = new ShellText(language: "ko");
        keys.Set("hasSession", true);

        Assert.False(keys.Evaluate(AiCommands.TuneEnablement));
        Assert.Equal("설정에서 AI 모델을 먼저 고르세요", Reason(keys, text));

        keys.Set(AiContextKeys.ConfiguredKey, true);
        Assert.Contains("허용 수준이 「끔」", Reason(keys, text), StringComparison.Ordinal);

        keys.Set(AiContextKeys.AccessOnKey, true);
        Assert.True(keys.Evaluate(AiCommands.TuneEnablement));
        Assert.False(keys.Evaluate("aiNotYet"));
    }

    private static string? Reason(ContextKeyService keys, ShellText text) =>
        ContextKeyReasons.Explain(AiCommands.TuneEnablement, keys.Evaluate, k => text.Lookup(k) is var v && v != k ? v : null);

    [Fact]
    public async Task Configured_key_follows_the_ai_settings_and_access_key_is_off_without_a_session()
    {
        var keys = new ContextKeyService();
        var settings = new SettingsService(new MemorySettingsStore());
        var container = new ServiceContainer().AddInstance<IContextKeys>(keys).AddInstance<IEventBus>(new EventBus()).AddInstance(settings);
        var task = new AiContextKeys(container);

        task.Sync();
        Assert.True(keys.Evaluate(AiContextKeys.ConfiguredKey)); // 기본 드라이버가 Claude Code

        await settings.UpdateAsync(s => s with { Ai = new AiSettings { Driver = AiDriverKind.None } });
        task.Sync();
        Assert.False(keys.Evaluate(AiContextKeys.ConfiguredKey));

        await settings.UpdateAsync(s => s with { Ai = new AiSettings { Driver = AiDriverKind.ClaudeCode } });
        task.Sync();
        Assert.True(keys.Evaluate(AiContextKeys.ConfiguredKey));
        Assert.False(keys.Evaluate(AiContextKeys.AccessOnKey));
    }

    [Fact]
    public async Task Settings_save_writes_the_ai_model_choice_and_the_tab_hides_without_the_feature()
    {
        var settings = new SettingsService(new MemorySettingsStore());
        var vm = new SettingsViewModel(settings, new ShellText(language: "ko"), featureGate: new FakeGate(aiTuning: false));
        Assert.False(vm.AiTabVisible);

        vm.AiModel.IsClaudeCode = true;
        vm.AiModel.Model = "sonnet";
        await vm.SaveAsync();

        Assert.Equal((AiDriverKind.ClaudeCode, "sonnet"), (settings.Current.Ai.Driver, settings.Current.Ai.Model));
        Assert.True(new SettingsViewModel(settings, new ShellText(language: "ko"), featureGate: new FakeGate(aiTuning: true)).AiTabVisible);
    }

    [Fact]
    public void Profile_editor_carries_the_ai_access_into_the_saved_profile_and_blocks_production_without_the_name()
    {
        var dev = ProfileEditorViewModel.From(Profile(ConnectionEnvironment.Development));
        dev.AiAccess.SelectLevelCommand.Execute(AiAccessLevel.QueryRows);
        var saved = dev.TryBuild(out var devErrors);
        Assert.NotNull(saved);
        Assert.Empty(devErrors);
        Assert.Equal(AiAccessLevel.QueryRows, saved.AiAccess);

        // 기본값(운영 Query)을 그대로 저장하는 편집은 이름을 묻지 않는다.
        var untouched = ProfileEditorViewModel.From(Profile(ConnectionEnvironment.Production));
        untouched.AiVisible = true;
        Assert.NotNull(untouched.TryBuild(out _));

        // 끔으로 저장돼 있던 운영 접속의 수준을 직접 올릴 때만 묻는다.
        var prod = ProfileEditorViewModel.From(Profile(ConnectionEnvironment.Production) with { AiAccess = AiAccessLevel.Off });
        prod.AiVisible = true;
        prod.AiAccess.SelectLevelCommand.Execute(AiAccessLevel.Plan);
        Assert.Null(prod.TryBuild(out var errors));
        Assert.Contains(errors, e => e.Contains("접속 이름을 다시 입력", StringComparison.Ordinal));

        prod.AiAccess.ConfirmName = "ERP";
        Assert.Equal(AiAccessLevel.Plan, prod.TryBuild(out _)!.AiAccess);
    }

    [Fact]
    public void New_production_connection_saves_without_the_name_because_the_default_is_the_ceiling()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.AiVisible = true;
        vm.Name = "PROD1";
        vm.User = "APP_OWNER";
        vm.Host = "db-example";
        vm.Service = "svc";
        vm.Environment = ConnectionEnvironment.Production;
        Assert.NotNull(vm.TryBuild(out _)); // 수준을 건드리지 않은 새 운영 접속은 이름을 묻지 않는다(기본 Query)

        // 운영의 기본이 상한이라 새 접속에서 올릴 길이 없다: 낮춰 고른 수준도 이름 없이 저장된다.
        vm.AiAccess.SelectLevelCommand.Execute(AiAccessLevel.Plan);
        Assert.Equal(AiAccessLevel.Plan, vm.TryBuild(out _)!.AiAccess);
    }

    [Fact]
    public void Hidden_ai_area_never_blocks_a_save_and_a_production_relabel_drops_the_raised_level()
    {
        var vm = ProfileEditorViewModel.From(Profile(ConnectionEnvironment.Development) with { AiAccess = AiAccessLevel.Metadata });
        vm.AiVisible = false; // 기능이 꺼진 상태(앱 밖). 기본값은 다른 시험이 남긴 앱의 게이트를 따르므로(aiTuning 기본 켬) 직접 정한다

        vm.Environment = ConnectionEnvironment.Production;

        var saved = vm.TryBuild(out var errors);
        Assert.NotNull(saved);
        Assert.Empty(errors);
        Assert.Null(saved.AiAccess); // 운영 기본값(Query)으로 돌아간다
    }

    [Fact]
    public void Changing_the_environment_in_the_profile_editor_lowers_the_ai_ceiling()
    {
        var vm = ProfileEditorViewModel.From(Profile(ConnectionEnvironment.Development));
        vm.AiAccess.SelectLevelCommand.Execute(AiAccessLevel.QueryRows);

        vm.Environment = ConnectionEnvironment.Production;

        Assert.Equal(AiAccessLevel.Query, vm.AiAccess.MaxLevel);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Bridge_starts_only_when_the_ai_tuning_feature_is_on()
    {
        var off = new FakeHost();
        Assert.False(AgentBridgeStartup.Start(Services(new FakeGate(aiTuning: false), off)));
        Assert.Equal(0, off.Starts);

        var on = new FakeHost();
        Assert.True(AgentBridgeStartup.Start(Services(new FakeGate(aiTuning: true), on)));
        Assert.Equal(1, on.Starts);
    }

    [Fact]
    public void Real_container_resolves_the_driver_gate_and_one_bridge_host()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-ai-" + Guid.NewGuid().ToString("N"));
        using var container = AppComposition.Create(new AppCompositionOptions { DataDirectory = dir });

        Assert.NotNull(container.GetService<IAiDriver>());
        Assert.NotNull(container.GetService<IAgentGate>());
        Assert.NotNull(container.GetService<IAgentBridgeHost>());
        Assert.Same(container.GetService<IAgentBridgeHost>(), container.GetService<IAgentBridgeHost>());
    }

    private static ServiceContainer Services(IFeatureGate gate, IAgentBridgeHost host) =>
        new ServiceContainer().AddInstance(gate).AddInstance(host);

    private static ConnectionProfile Profile(ConnectionEnvironment environment) =>
        new("ERP", "APP_OWNER", "db-example", Environment: environment);

    private sealed class FakeHost : IAgentBridgeHost
    {
        public int Starts { get; private set; }

        public string Start()
        {
            Starts++;
            return "pipe-example";
        }
    }

    private sealed class FakeGate(bool aiTuning) : IFeatureGate
    {
        public event EventHandler? Changed { add { } remove { } }

        public bool ReadOnlyRole => false;

        public IReadOnlyCollection<string> PendingRestart => [];

        public bool IsEnabled(string featureId) => featureId == FeatureIds.AiTuning && aiTuning;

        public bool IsCommandEnabled(string commandId, Type handlerType) => true;

        public bool IsPanelEnabled(string panelId, Type viewModelType) => true;

        public bool IsTypeEnabled(Type registeredType) => true;
    }
}
