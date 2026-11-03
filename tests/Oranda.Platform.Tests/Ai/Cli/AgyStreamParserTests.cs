using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>탐침에서 녹화한 agy stream-json 표본(Fixtures/agy-*.jsonl)으로 읽기 규칙과 잠금 감시를 고정한다.</summary>
public sealed class AgyStreamParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-parser-" + Guid.NewGuid().ToString("N"));

    public AgyStreamParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [Fact]
    public void Init_gives_the_conversation_id_and_does_not_expose_the_tool_list()
    {
        var events = AgyStreamParser.Parse(Fixture("agy-denied-read-file.jsonl")[0]);

        Assert.Equal("50877f25-9b32-4f2d-aa19-f4577e62f91b", Assert.IsType<AgySessionStarted>(Assert.Single(events)).ConversationId);
    }

    [Fact]
    public void Recorded_steps_are_only_user_input_agent_response_and_tool_which_are_the_allowed_kinds()
    {
        var all = Fixture("agy-denied-read-file.jsonl").Concat(Fixture("agy-builtin-tools.jsonl")).Concat(Fixture("agy-read-then-command.jsonl"))
            .SelectMany(AgyStreamParser.Parse)
            .ToList();

        Assert.DoesNotContain(all, e => e is AgyUnknownStep);
        Assert.Equal(["agent_response", "finish", "tool", "user_input"], AgyStreamParser.AllowedStepTypes.Order());
    }

    [Fact]
    public void A_tool_step_exposes_its_name_state_and_error_message()
    {
        var steps = Fixture("agy-denied-read-file.jsonl").SelectMany(AgyStreamParser.Parse).OfType<AgyToolStep>().ToList();

        Assert.Equal(["ACTIVE", "ERROR"], steps.Select(s => s.State));
        Assert.All(steps, s => Assert.Equal("read_file", s.ToolName));
        Assert.Contains("permission check failed", steps[1].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_gives_status_usage_and_has_no_finished_reason_on_a_normal_end()
    {
        var result = Fixture("agy-denied-read-file.jsonl").SelectMany(AgyStreamParser.Parse).OfType<AgyResult>().Single();

        Assert.Equal(("SUCCESS", null), (result.Status, result.FinishedReason));
        Assert.Equal((7947, 628), (result.InputTokens, result.OutputTokens));
    }

    [Fact]
    public void Non_json_lines_are_skipped_but_unknown_events_and_malformed_steps_are_unknown_steps_that_the_watch_closes()
    {
        Assert.Empty(AgyStreamParser.Parse("not json"));
        Assert.Empty(AgyStreamParser.Parse(string.Empty));
        Assert.IsType<AgyUnknownStep>(Assert.Single(AgyStreamParser.Parse("""{"event":"something_new","x":1}""")));
        Assert.IsType<AgyUnknownStep>(Assert.Single(AgyStreamParser.Parse("""{"event":"step_update"}""")));
        Assert.IsType<AgyUnknownStep>(Assert.Single(AgyStreamParser.Parse("""{"event":"step_update","step_update":"x"}""")));
        Assert.IsType<AgyUnknownStep>(Assert.Single(AgyStreamParser.Parse("""{"event":"init"}""")));
    }

    [Fact]
    public async Task The_recorded_builtin_tool_streams_are_closed_at_the_first_builtin_tool_with_unexpected_tool()
    {
        foreach (var name in new[] { "agy-denied-read-file.jsonl", "agy-builtin-tools.jsonl", "agy-read-then-command.jsonl" })
        {
            var tokens = new FakeRunTokens();
            var gate = new FakeGate();
            var launcher = new FakeLauncher { Lines = Fixture(name) };
            var agy = Path.Combine(_dir, OperatingSystem.IsWindows() ? "agy.exe" : "agy"); // Windows 는 이름.exe 를 찾는다
            var orandactl = Path.Combine(_dir, "orandactl");
            var driver = new AgyDriver(
                tokens, gate, new FixedEndpoint("pipe"), new AppDataPaths(_dir), launcher, orandactl,
                () => _dir, path => path == agy || path == orandactl, _dir, TimeSpan.FromMinutes(1), (_, _, _) => Task.FromResult<string?>("1.4.12"));

            var events = new List<AiEvent>();
            await foreach (var e in driver.RunAsync(Request(), CancellationToken.None))
            {
                events.Add(e);
            }

            Assert.Equal("unexpected_tool", Assert.IsType<AiError>(events[^1]).Code);
            Assert.True(launcher.Process!.Killed);
            Assert.DoesNotContain(events, e => e is AiFinal or AiToolCall);
        }
    }

    private static string[] Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllLines(Path.Combine(directory!.FullName, "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name));
    }

    private static AiRequest Request()
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest("the prompt", context, Session: new AgentSession(new AgentRunScope("run-1", profile, context)));
    }
}
