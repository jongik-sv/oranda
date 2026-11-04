using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Platform.Ai;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai;

/// <summary>
/// 기타 CLI 선택에서 라우터가 보내기 전에 거르는 조건. 드라이버 대역이 호출됐는지로 「보냈는가」 를 확인한다
/// (머지 시점에는 실제 드라이버가 없으므로 등록이 없을 때 닫히는 쪽도 함께 고정한다).
/// </summary>
public sealed class RoutingToCustomCliTests
{
    private static readonly AiCustomCli Mine = new() { Id = "c1", Name = "사내 CLI", Command = "mycli --quiet" };

    private static readonly AiContext PlanContext = new("p", ConnectionEnvironment.Development, AiAccessLevel.Plan);

    private static readonly AiRequest Sendable = new("질문", PlanContext, MaskingRequired: false);

    private readonly FakeSettingsStore _settings = new();

    private void Use(AiCustomCli? cli, string? selection = null, bool maskLiterals = false, AiDriverKind driver = AiDriverKind.ClaudeCode) =>
        _settings.Current = new AppSettings
        {
            Ai = new AiSettings { Driver = driver, SystemCliId = selection ?? (cli is null ? null : AiCustomCliTarget.SelectionOf(cli.Id)), CustomClis = cli is null ? [] : [cli], MaskLiterals = maskLiterals },
        };

    private RoutingAiDriver Create(ICustomCliAiDriver? custom)
    {
        var cli = new CliDriver(new FakeRunTokens(), new FakeGate(), new FixedBridgeEndpoint("pipe"), new AppDataPaths(Path.GetTempPath()), _settings);
        return new RoutingAiDriver(_settings, new FakeCredentialStore(), cli, new Services(custom));
    }

    // 화면에서 명령을 확인한 요청(라우터는 확인한 명령이 있어야 보낸다).
    private static AiRequest Confirmed() => Sendable with { Expected = new AiProfileExpectation(null, SystemCliId: "custom:c1", CustomCliCommandHash: AiCustomCliTarget.CommandHash(Mine.Command)) };

    private static async Task<List<AiEvent>> RunAsync(RoutingAiDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    private static string? CodeOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(Assert.Single(events)).Code;

    private static AgentSession SessionOn(ConnectionEnvironment environment) =>
        new(new AgentRunScope("r", new ConnectionProfile("p", "u", "h/s", Environment: environment), new AiContext("p", environment, AiAccessLevel.Query)));

    [Fact]
    public async Task A_confirmed_unmasked_request_reaches_the_driver_with_the_row_and_no_claude_values()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        var events = await RunAsync(Create(fake), Confirmed() with { Model = "opus", Effort = "max" });

        Assert.IsType<AiFinal>(Assert.Single(events));
        var (request, row) = Assert.Single(fake.Calls);
        Assert.Equal(Mine, row);
        Assert.Null(request.Model);
        Assert.Null(request.Effort);
    }

    [Fact]
    public async Task Without_a_registered_driver_it_closes_as_unavailable_not_as_claude_code()
    {
        Use(Mine);

        Assert.Equal("custom_cli_unavailable", CodeOf(await RunAsync(Create(null), Confirmed())));
    }

    [Theory]
    [InlineData("custom:nope")]
    [InlineData("custom:")]
    public async Task A_missing_row_is_closed_and_starts_nothing(string selection)
    {
        var fake = new FakeCustomDriver();
        Use(Mine, selection);

        Assert.Equal("custom_cli_unavailable", CodeOf(await RunAsync(Create(fake), Sendable)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task An_empty_command_is_closed_and_starts_nothing()
    {
        var fake = new FakeCustomDriver();
        Use(Mine with { Command = "  " });

        Assert.Equal("custom_cli_unavailable", CodeOf(await RunAsync(Create(fake), Sendable)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_request_that_is_not_confirmed_unmasked_is_blocked()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), Confirmed() with { MaskingRequired = true })));
        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), Confirmed() with { MaskingRequired = null })));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_request_with_images_is_refused_by_the_router_before_the_driver()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        var code = CodeOf(await RunAsync(Create(fake), Confirmed() with { Images = [new AiImageAttachment("image/png", [1, 2, 3])] }));

        Assert.Equal(RoutingAiDriver.CustomCliImagesUnsupportedCode, code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_global_masking_setting_blocks_even_without_a_connection()
    {
        var fake = new FakeCustomDriver();
        Use(Mine, maskLiterals: true);

        var request = Confirmed() with { Context = new AiContext(null, null, AiAccessLevel.Off) };

        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), request)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_production_context_or_session_blocks_even_when_the_caller_says_unmasked()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);
        var production = new AiContext("p", ConnectionEnvironment.Production, AiAccessLevel.Query);

        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), Confirmed() with { Context = production })));
        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), Confirmed() with { Session = SessionOn(ConnectionEnvironment.Production) })));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Changing_only_the_command_after_confirmation_is_profile_changed()
    {
        var fake = new FakeCustomDriver();
        var confirmed = new AiProfileExpectation(null, SystemCliId: "custom:c1", CustomCliCommandHash: AiCustomCliTarget.CommandHash(Mine.Command));
        Use(Mine with { Command = "mycli --auto-approve" });

        Assert.Equal("profile_changed", CodeOf(await RunAsync(Create(fake), Sendable with { Expected = confirmed })));
        Assert.Empty(fake.Calls);

        Use(Mine);
        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(Create(fake), Sendable with { Expected = confirmed })));
    }

    [Fact]
    public async Task A_request_without_any_expectation_is_not_sent()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        Assert.Equal("profile_changed", CodeOf(await RunAsync(Create(fake), Sendable)));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_tool_session_is_not_handed_to_the_driver_and_a_session_masking_requirement_blocks()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);
        var development = SessionOn(ConnectionEnvironment.Development);

        Assert.IsType<AiFinal>(Assert.Single(await RunAsync(Create(fake), Confirmed() with { Session = development })));
        Assert.Null(Assert.Single(fake.Calls).Request.Session);

        var masked = new AgentSession(development.Run with { MaskLiteralsRequired = true });
        Assert.Equal(RoutingAiDriver.CustomCliMaskingBlockedCode, CodeOf(await RunAsync(Create(fake), Confirmed() with { Session = masked })));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task An_expectation_without_a_command_hash_is_not_the_same_target()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        var code = CodeOf(await RunAsync(Create(fake), Sendable with { Expected = new AiProfileExpectation(null, SystemCliId: "custom:c1") }));

        Assert.Equal("profile_changed", code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task An_expectation_of_another_cli_than_the_setting_is_profile_changed()
    {
        var fake = new FakeCustomDriver();
        Use(Mine);

        Assert.Equal("profile_changed", CodeOf(await RunAsync(Create(fake), Sendable with { Expected = new AiProfileExpectation(null) })));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Driver_none_stays_closed_even_with_a_custom_selection()
    {
        var fake = new FakeCustomDriver();
        Use(Mine, driver: AiDriverKind.None);

        Assert.Equal("driver_none", CodeOf(await RunAsync(Create(fake), Sendable)));
        Assert.Empty(fake.Calls);
    }

    private sealed class FakeCustomDriver : ICustomCliAiDriver
    {
        public List<(AiRequest Request, AiCustomCli Cli)> Calls { get; } = [];

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiCustomCli cli, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add((request, cli));
            await Task.CompletedTask;
            yield return new AiFinal("답", null, null);
        }
    }

    private sealed class Services(ICustomCliAiDriver? custom) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ICustomCliAiDriver) ? custom : null;
    }
}
