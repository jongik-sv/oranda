using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Entry;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Entry;

/// <summary>진입점 3곳(W3): 도구 메뉴 명령·첫 실행 한 번 묻기·AI 실패 띠 단추. 런처는 가짜.</summary>
public sealed class AiSetupEntryTests
{
    private sealed class FakeLauncher : IAiSetupLauncher
    {
        public List<(AiSetupEntry Entry, AiSetupErrorKind? Reason)> Opened { get; } = [];

        public Task OpenAsync(AiSetupEntry entry, AiSetupErrorKind? reason, CancellationToken ct)
        {
            Opened.Add((entry, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClaude(ClaudeInstallation? found) : IClaudeSetupService
    {
        public int Finds { get; private set; }

        public Task<ClaudeInstallation?> FindAsync(CancellationToken ct)
        {
            Finds++;
            return Task.FromResult(found);
        }

        public Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct) => throw new NotSupportedException();

        public Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Services(Dictionary<Type, object> map) : IServiceProvider
    {
        public object? GetService(Type serviceType) => map.GetValueOrDefault(serviceType);
    }

    private static readonly AiLlmProfile Profile = new() { Id = Guid.NewGuid(), Name = "사내", Preset = AiProfilePreset.Custom, Format = AiApiFormat.OpenAi, BaseUrl = "https://ai.example.com/v1", Model = "m" };

    private static (Services Services, SettingsService Settings, FakeLauncher Launcher, InMemorySettingsStore Store) Rig(
        AiSettings? ai = null, IClaudeSetupService? claude = null, bool aiTuning = true, bool launcher = true)
    {
        var store = new InMemorySettingsStore { Current = new AppSettings { Ai = ai ?? new AiSettings() } };
        var settings = new SettingsService(store);
        settings.LoadAsync().GetAwaiter().GetResult();
        var fake = new FakeLauncher();
        var map = new Dictionary<Type, object>
        {
            [typeof(SettingsService)] = settings,
            [typeof(IFeatureGate)] = new FakeFeatureGate(aiTuning ? FeatureIds.All : FeatureIds.All.Where(f => f != FeatureIds.AiTuning)),
        };
        if (launcher)
        {
            map[typeof(IAiSetupLauncher)] = fake;
        }

        if (claude is not null)
        {
            map[typeof(IClaudeSetupService)] = claude;
        }

        return (new Services(map), settings, fake, store);
    }

    [Fact]
    public async Task Menu_command_opens_the_launcher_and_does_nothing_without_one()
    {
        var (services, _, launcher, _) = Rig();
        await new AiSetupOpenCommandHandler(services).ExecuteAsync(new CommandContext(AiSetupIds.OpenCommand, new Oranda.App.Shell.ContextKeyService()), CancellationToken.None);
        Assert.Equal([(AiSetupEntry.Menu, (AiSetupErrorKind?)null)], launcher.Opened);

        var (bare, _, unused, _) = Rig(launcher: false);
        await new AiSetupOpenCommandHandler(bare).ExecuteAsync(new CommandContext(AiSetupIds.OpenCommand, new Oranda.App.Shell.ContextKeyService()), CancellationToken.None);
        await new AiSetupOpenCommandHandler().ExecuteAsync(new CommandContext(AiSetupIds.OpenCommand, new Oranda.App.Shell.ContextKeyService()), CancellationToken.None);
        Assert.Empty(unused.Opened);
    }

    [Fact]
    public void Ai_settings_is_the_one_common_menu_line_and_the_assistant_and_agent_commands_are_palette_only()
    {
        // ai-settings-one-place: 도구 메뉴에는 「AI 설정…」 한 줄(AI 기능과 무관하게 보임). 도우미·외부 에이전트 연결은 팔레트·설정 「AI」 탭으로만.
        var settings = (OrandaCommandAttribute)Attribute.GetCustomAttribute(typeof(AiSettingsOpenCommandHandler), typeof(OrandaCommandAttribute))!;
        Assert.Equal(AiSetupIds.SettingsCommand, settings.Id);
        Assert.Equal("oranda.ai.settings.open", settings.Id);
        Assert.Equal(["menu:tools/externalAgent@44"], settings.Placements);
        Assert.Null(FeatureCatalog.FeatureFor(settings.Id, typeof(AiSettingsOpenCommandHandler)));

        var assistant = (OrandaCommandAttribute)Attribute.GetCustomAttribute(typeof(AiSetupOpenCommandHandler), typeof(OrandaCommandAttribute))!;
        Assert.Equal(AiSetupIds.OpenCommand, assistant.Id);
        Assert.True(assistant.Placements is null or { Length: 0 });
        var agent = (OrandaCommandAttribute)Attribute.GetCustomAttribute(typeof(Oranda.App.Features.Ai.External.Connect.ExternalAgentConnectCommandHandler), typeof(OrandaCommandAttribute))!;
        Assert.True(agent.Placements is null or { Length: 0 });
    }

    [Fact]
    public void Ai_settings_command_opens_the_settings_window_on_the_ai_tab()
    {
        var ui = new RecordingUi();
        new AiSettingsOpenCommandHandler(ui).ExecuteAsync(new CommandContext(AiSetupIds.SettingsCommand, new Oranda.App.Shell.ContextKeyService()), CancellationToken.None);
        var vm = new Oranda.App.Shell.Settings.SettingsViewModel(new Oranda.App.Shell.Settings.SettingsService(new Oranda.App.Tests.Features.Update.MemorySettingsStore()), new Oranda.App.Shell.ShellText(language: "ko"));
        Assert.NotNull(ui.Prepare);
        ui.Prepare!(vm);
        Assert.Equal(Oranda.App.Shell.Settings.SettingsViewModel.AiTabIndex, vm.SelectedTab);
    }

    private sealed class RecordingUi : Oranda.App.Shell.IShellUi
    {
        public Action<Oranda.App.Shell.Settings.SettingsViewModel>? Prepare { get; private set; }

        public Oranda.App.Shell.Docking.DockWorkspace Docking => throw new NotSupportedException();

        public Oranda.App.Shell.Settings.SettingsService Settings => throw new NotSupportedException();

        public Oranda.Core.Connection.ConnectionEnvironment? ActiveEnvironment { get; set; }

        public void ShowCommandPalette() { }

        public void ShowSettings() { }

        public void ShowSettings(Action<Oranda.App.Shell.Settings.SettingsViewModel>? prepare) => Prepare = prepare;

        public void ShowAbout() { }

        public void CloseMainWindow() { }

        public void FocusDocument() { }
    }

    [Fact]
    public Task First_run_without_a_window_to_ask_on_does_not_even_look_for_claude() => HeadlessHost.Run(async () =>
    {
        var missing = new FakeClaude(null);
        var (services, _, _, store) = Rig(claude: missing);
        var asked = 0;

        await new AiSetupFirstRunPrompt(services) { CanAsk = () => false, Ask = _ => { asked++; return Task.FromResult<bool?>(true); } }.RunAsync(CancellationToken.None);

        Assert.Equal(0, missing.Finds);
        Assert.Equal(0, asked);
        Assert.False(store.Current.Ai.Setup.FirstRunPromptClosed);
    });

    [Fact]
    public async Task First_run_that_could_not_ask_does_not_close_the_question()
    {
        var (services, settings, launcher, store) = Rig(claude: new FakeClaude(null));
        var prompt = new AiSetupFirstRunPrompt(services) { CanAsk = () => true, Ask = _ => Task.FromResult<bool?>(null) };

        await prompt.AskOnceAsync(settings, CancellationToken.None);

        Assert.False(store.Current.Ai.Setup.FirstRunPromptClosed);
        Assert.Empty(launcher.Opened);
    }

    [Fact]
    public Task First_run_task_hands_the_question_to_the_ui_thread_without_waiting_for_the_answer() => HeadlessHost.Run(async () =>
    {
        var (services, _, _, _) = Rig(claude: new FakeClaude(null));
        var answer = new TaskCompletionSource<bool?>();
        var asked = 0;
        var prompt = new AiSetupFirstRunPrompt(services) { CanAsk = () => true, Ask = _ => { asked++; return answer.Task; } };

        await prompt.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); // 답을 기다리지 않고 끝난다

        answer.SetResult(false);
        Assert.InRange(asked, 0, 1);
    });

    [Fact]
    public void First_run_asks_only_when_every_condition_holds()
    {
        var claudeCode = new AiSettings();
        Assert.True(AiSetupFirstRunPrompt.MayAsk(true, claudeCode));
        Assert.False(AiSetupFirstRunPrompt.MayAsk(false, claudeCode)); // aiTuning 꺼짐
        Assert.False(AiSetupFirstRunPrompt.MayAsk(true, claudeCode with { Driver = AiDriverKind.None })); // 선택 안 함
        Assert.False(AiSetupFirstRunPrompt.MayAsk(true, claudeCode with { Profiles = [Profile], ActiveProfileId = Profile.Id })); // 프로필 고름
        Assert.False(AiSetupFirstRunPrompt.MayAsk(true, claudeCode with { SystemCliId = "codex-cli" })); // 다른 시스템 CLI
        Assert.False(AiSetupFirstRunPrompt.MayAsk(true, claudeCode with { Setup = new AiSetupSettings { FirstRunPromptClosed = true } })); // 닫음
    }

    [Fact]
    public Task First_run_does_not_ask_when_claude_is_found_or_cannot_be_looked_for() => HeadlessHost.Run(async () =>
    {
        var found = new FakeClaude(new ClaudeInstallation(@"C:\u\.local\bin\claude.exe", "2.1.330"));
        var (services, _, launcher, _) = Rig(claude: found);
        var asked = 0;
        await new AiSetupFirstRunPrompt(services) { CanAsk = () => true, Ask = _ => { asked++; return Task.FromResult<bool?>(true); } }.RunAsync(CancellationToken.None);
        Assert.Equal(1, found.Finds);

        var (noService, _, _, _) = Rig();
        await new AiSetupFirstRunPrompt(noService) { CanAsk = () => true, Ask = _ => { asked++; return Task.FromResult<bool?>(true); } }.RunAsync(CancellationToken.None);

        var missing = new FakeClaude(null);
        var (off, _, _, _) = Rig(claude: missing, aiTuning: false);
        await new AiSetupFirstRunPrompt(off) { CanAsk = () => true, Ask = _ => { asked++; return Task.FromResult<bool?>(true); } }.RunAsync(CancellationToken.None);
        Assert.Equal(0, missing.Finds); // 조건이 안 맞으면 찾지도 않는다

        Assert.Equal(0, asked);
        Assert.Empty(launcher.Opened);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_answer_closes_the_question_for_good_and_open_starts_the_assistant(bool open)
    {
        var (services, settings, launcher, store) = Rig(claude: new FakeClaude(null));
        var asked = 0;
        var prompt = new AiSetupFirstRunPrompt(services) { CanAsk = () => true, Ask = _ => { asked++; return Task.FromResult<bool?>(open); } };

        await prompt.AskOnceAsync(settings, CancellationToken.None);
        await prompt.AskOnceAsync(settings, CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.True(store.Current.Ai.Setup.FirstRunPromptClosed);
        Assert.False(AiSetupFirstRunPrompt.MayAsk(true, settings.Current.Ai));
        Assert.Equal(open ? [(AiSetupEntry.FirstRun, (AiSetupErrorKind?)AiSetupErrorKind.CliNotFound)] : [], launcher.Opened);
    }

    [Theory]
    [InlineData("cli_not_found", AiActiveKind.ClaudeCode, true)]
    [InlineData("cli_not_logged_in", AiActiveKind.ClaudeCode, true)]
    [InlineData("cli_not_found", null, true)]
    [InlineData("driver_none", AiActiveKind.None, true)]
    [InlineData(null, AiActiveKind.None, true)]
    [InlineData("network", AiActiveKind.None, false)] // 요청 중 설정이 바뀌어도 다른 오류에는 띠를 붙이지 않는다
    [InlineData("cli_not_found", AiActiveKind.SystemCli, false)] // 다른 CLI 를 못 찾음 = 도우미로 못 푼다
    [InlineData("cli_not_found", AiActiveKind.CustomCli, false)]
    [InlineData("network", AiActiveKind.ClaudeCode, false)]
    [InlineData("profile_missing", AiActiveKind.ProfileMissing, false)]
    [InlineData(null, AiActiveKind.ClaudeCode, false)]
    public void Band_button_shows_only_for_setup_codes(string? code, AiActiveKind? kind, bool shown) =>
        Assert.Equal(shown, AiSetupOffer.ShouldOffer(code, kind));

    [Fact]
    public async Task Chat_error_line_gets_the_band_only_for_setup_codes_and_the_button_opens_the_launcher()
    {
        var opened = new List<AiSetupErrorKind?>();
        var chat = new AiChatViewModel { SettingsProvider = () => new AiSettings(), OpenSetup = r => { opened.Add(r); return Task.FromResult(true); } };

        chat.Apply(new AiError("이 PC 에서 로그인하지 않았습니다.", AiSetupErrorCodes.CliNotLoggedIn));
        var band = Assert.IsType<ChatSetupEntry>(chat.Entries[^1]);
        Assert.Equal(AiSetupErrorKind.NotLoggedIn, band.Reason);
        await chat.OpenSetupCommand.ExecuteAsync(band);
        Assert.Equal([AiSetupErrorKind.NotLoggedIn], opened);

        var count = chat.Entries.Count;
        chat.Apply(new AiError("연결 실패", "network"));
        Assert.Equal(count + 1, chat.Entries.Count); // 오류 글만, 띠 없음
        Assert.IsType<ChatAssistantEntry>(chat.Entries[^1]);
    }

    [Fact]
    public void Chat_unavailable_line_shows_the_button_only_for_no_model()
    {
        var chat = new AiChatViewModel { SettingsProvider = () => new AiSettings { Driver = AiDriverKind.None }, UnavailableProvider = () => "선택 안 함" };
        Assert.True(chat.ShowsSetupForUnavailable);

        chat.SettingsProvider = () => new AiSettings { ActiveProfileId = Guid.NewGuid() };
        chat.UnavailableProvider = () => "프로필 없음";
        Assert.False(chat.ShowsSetupForUnavailable);
        Assert.False(chat.OfferSetup(errorCode: null));
    }

    [Fact]
    public async Task Inline_strip_shows_the_button_when_the_model_is_not_chosen_or_claude_is_not_logged_in()
    {
        var opened = new List<AiSetupErrorKind?>();
        var refused = new AiInlineStripViewModel("풀이", _ => Task.FromResult(AiStripPreparation.Refuse(AiEntryRule.NotConfiguredText)))
        {
            OpenSetup = r => { opened.Add(r); return Task.FromResult(true); },
            ActiveKind = _ => AiActiveKind.None,
        };
        await refused.RunAsync(CancellationToken.None);
        Assert.True(refused.ShowsSetup);
        await refused.OpenSetupCommand.ExecuteAsync(null);
        Assert.Equal([(AiSetupErrorKind?)null], opened);

        var other = new AiInlineStripViewModel("풀이", _ => Task.FromResult(AiStripPreparation.Refuse("고른 사슬이 없습니다"))) { ActiveKind = _ => AiActiveKind.ClaudeCode };
        await other.RunAsync(CancellationToken.None);
        Assert.False(other.ShowsSetup);
        Assert.False(other.OpenSetupCommand.CanExecute(null));

        var run = KitFakes.Run(new FakeDriver { Answer = _ => [new AiError("로그인 필요", AiSetupErrorCodes.CliNotLoggedIn)] });
        var failed = new AiInlineStripViewModel("풀이", _ => Task.FromResult(AiStripPreparation.Ready(new AiStripRequest(run, run.Route.Bundle("풀이", null).Build(), "x"))))
        {
            OpenSetup = r => { opened.Add(r); return Task.FromResult(true); },
            ActiveKind = _ => AiActiveKind.ClaudeCode,
        };
        await failed.RunAsync(CancellationToken.None);
        Assert.Equal(AiStripState.Failed, failed.State);
        Assert.True(failed.ShowsSetup);
        await failed.OpenSetupCommand.ExecuteAsync(null);
        Assert.Equal(AiSetupErrorKind.NotLoggedIn, opened[^1]);
    }

    [Fact]
    public void Advanced_model_settings_no_longer_carry_a_second_assistant_link()
    {
        // ai-settings-one-place 결정 4: 도우미는 탭 ②의 「다시 연결 (도우미)」 한 곳에서 연다.
        Assert.Null(typeof(AiModelSettingsPage).GetProperty("SetupLinkText"));
        Assert.Null(typeof(AiModelSettingsPage).GetProperty("OpenSetupCommand"));
    }
}
