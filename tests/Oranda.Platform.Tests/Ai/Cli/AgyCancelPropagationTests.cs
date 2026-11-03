using System.Diagnostics;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;
using Xunit.Abstractions;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 운영 DB 안전 측정: 오래 걸리는 도구 호출이 끝나기 전에 agy 가 포기하거나 사용자가 중지하거나 실행 제한이 지나면,
/// 게이트(<see cref="IAgentGate.CallToolAsync"/>)의 두 토큰이 언제 취소되는가: 브리지 <c>ct</c>(연결 토큰)와 <see cref="AgentSession.RevocationToken"/>
/// (실제 <c>AgentGate</c> 가 <c>ct</c> 와 묶어 처리자·DB 호출에 넘기는 값). 실행이 끝난 뒤에도 호스트를 유지한 채 본다(앱의 브리지는 앱 수명 동안 산다).
/// 실제 agy·실제 orandactl·실제 브리지에 가짜 모델 서버를 쓰고 게이트만 느린 대체품으로 바꾼다. <c>ORANDA_AI_E2E_AGY=1</c> 일 때만 돈다.
/// </summary>
public sealed class AgyCancelPropagationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-cancel-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// 측정 결과의 고정: agy 가 5초 제한으로 호출을 포기해도 처리자 토큰(세션 폐기 토큰)은 그때 취소되지 않고 실행이 끝나 세션을 폐기할 때 취소된다.
    /// 브리지 <c>ct</c> 는 연결(파이프 서버) 토큰이라 실행이 끝나도 취소되지 않는다. 같은 시각에 다음 호출은 브리지 호출 대기열(<c>_callGate</c>)에 막혀
    /// 게이트에 도착하지 못한다. 이 동작이 바뀌면(연결 끊김 감시가 생기면) 이 시험이 알려 준다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task When_agy_gives_up_on_the_tool_call_only_the_session_revocation_token_cancels_and_only_when_the_run_ends()
    {
        var gate = new HangingGate();
        var (events, elapsed) = await RunAsync(gate, toolTimeoutSeconds: 5, runLimit: TimeSpan.FromMinutes(5), cancelAfter: null);

        output.WriteLine($"[agy 포기] 브리지 ct 취소 {Seconds(gate.CancelledAfter)}, 처리자(세션 폐기) 토큰 취소 {Seconds(gate.HandlerCancelledAfter)}, 호출 도착 시각 [{gate.EntriesText()}], 실행 전체 {elapsed.TotalSeconds:F2}초(실행 끝 +3초 뒤 상태), 이벤트 {events.Count}건");
        Assert.True(gate.Entered, "게이트에 도구 호출이 도착하지 않았다.");
        Assert.NotNull(gate.HandlerCancelledAfter);

        // 포기 시각(5초)에는 취소되지 않았고, 실행이 끝날 때(폐기) 취소됐다.
        Assert.True(gate.HandlerCancelledAfter!.Value > TimeSpan.FromSeconds(7), "agy 포기 시점에 처리자 토큰이 취소됐다: " + gate.HandlerCancelledAfter);
        Assert.Null(gate.CancelledAfter);

        // 첫 호출이 게이트에서 끝나지 않는 동안 다음 호출은 게이트에 도착하지 못했다.
        Assert.Single(gate.Entries);
    }

    [AgyEndToEndFact]
    public async Task A_user_stop_cancels_the_handler_token_at_once_but_not_the_bridge_ct()
    {
        var gate = new HangingGate();
        var (events, elapsed) = await RunAsync(gate, toolTimeoutSeconds: 600, runLimit: TimeSpan.FromMinutes(5), cancelAfter: TimeSpan.FromSeconds(8));

        output.WriteLine($"[사용자 중지] 브리지 ct 취소 {Seconds(gate.CancelledAfter)}, 처리자(세션 폐기) 토큰 취소 {Seconds(gate.HandlerCancelledAfter)}, 호출 도착 시각 [{gate.EntriesText()}], 실행 전체 {elapsed.TotalSeconds:F2}초(실행 끝 +3초 뒤 상태), 이벤트 {events.Count}건");
        Assert.True(gate.Entered);
        Assert.NotNull(gate.HandlerCancelledAfter);
        Assert.InRange(gate.HandlerCancelledAfter!.Value, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)); // 중지 8초 + 정리 시간
        Assert.Null(gate.CancelledAfter);
        Assert.DoesNotContain(events, e => e is AiError { Code: "cli_timeout" });
    }

    [AgyEndToEndFact]
    public async Task Exceeding_the_run_limit_cancels_the_handler_token_the_same_way_and_reports_a_timeout()
    {
        var gate = new HangingGate();
        var (events, elapsed) = await RunAsync(gate, toolTimeoutSeconds: 600, runLimit: TimeSpan.FromSeconds(8), cancelAfter: null);

        output.WriteLine($"[실행 제한 초과] 브리지 ct 취소 {Seconds(gate.CancelledAfter)}, 처리자(세션 폐기) 토큰 취소 {Seconds(gate.HandlerCancelledAfter)}, 호출 도착 시각 [{gate.EntriesText()}], 실행 전체 {elapsed.TotalSeconds:F2}초(실행 끝 +3초 뒤 상태), 이벤트 {events.Count}건");
        Assert.True(gate.Entered);
        Assert.NotNull(gate.HandlerCancelledAfter);
        Assert.InRange(gate.HandlerCancelledAfter!.Value, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(10)); // 제한 8초 − 첫 호출 도착 지연 + 정리 시간
        Assert.Null(gate.CancelledAfter);
        Assert.Contains(events, e => e is AiError { Code: "cli_timeout" });
    }

    private static string Seconds(TimeSpan? value) => value is { } t ? t.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "초" : "취소 안 됨";

    private async Task<(List<AiEvent> Events, TimeSpan Elapsed)> RunAsync(HangingGate gate, int toolTimeoutSeconds, TimeSpan runLimit, TimeSpan? cancelAfter)
    {
        Directory.CreateDirectory(_dir);
        var tokens = new FakeRunTokens();
        var unique = Guid.NewGuid().ToString("N");
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oc-{unique[..8]}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();

        var decoyHome = Path.Combine(_dir, "home");
        Directory.CreateDirectory(decoyHome);
        using var server = new AgyEndToEndTests.FakeModelServer(
        [
            ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "describe_object", ["Arguments"] = new JsonObject { ["object"] = "EMP" } }),
            ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "describe_object", ["Arguments"] = new JsonObject { ["object"] = "EMP" } }),
            ("finish", new JsonObject
            {
                ["summary"] = "Example summary.",
                ["rewrites"] = new JsonArray(),
                ["index_suggestions"] = new JsonArray(),
                ["evidence"] = new JsonArray(),
            }),
        ]);
        var driver = new AgyDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            new FakeModelLauncher(server.BaseUrl, decoyHome),
            FindOrandactl(),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            home: decoyHome,
            runLimit,
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, AgyDriver.LaunchEnvironment(null), ct))
        {
            ToolTimeoutSeconds = toolTimeoutSeconds,
        };

        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        var request = new AiRequest("Tune: SELECT 1 FROM dual", context, OutputSchema: TuningResult.Schema(), Session: new AgentSession(new AgentRunScope("cancel-run", profile, context)));

        using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var userStop = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        var events = new List<AiEvent>();
        var watch = Stopwatch.StartNew();
        try
        {
            if (cancelAfter is { } delay)
            {
                _ = Task.Run(async () =>
                {
                    await gate.EnteredSignal.Task.WaitAsync(overall.Token);
                    await Task.Delay(delay, overall.Token);
                    await userStop.CancelAsync();
                });
            }

            await foreach (var e in driver.RunAsync(request, userStop.Token))
            {
                events.Add(e);
            }
        }
        catch (OperationCanceledException) when (userStop.IsCancellationRequested && !overall.IsCancellationRequested)
        {
            // 사용자 중지는 취소 예외로 끝난다.
        }

        watch.Stop();

        // 실행이 끝난 뒤에도 호스트(앱 수명 동안 사는 브리지)를 3초 더 유지한 채 본다. 호스트 해제는 연결 토큰을 취소하므로 그 전에 상태를 붙잡는다.
        await Task.Delay(TimeSpan.FromSeconds(3));
        gate.Freeze();
        Assert.False(overall.IsCancellationRequested, "전체 시험 시간(3분) 안에 끝나지 않았다.");
        Assert.Equal(tokens.Issued, tokens.Revoked);
        return (events, watch.Elapsed);
    }

    private static string FindOrandactl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        var exe = OperatingSystem.IsWindows() ? "orandactl.exe" : "orandactl";
        var path = Path.Combine(directory!.FullName, "src", "Oranda.Agent.Cli", "bin", "Debug", "net10.0", exe);
        return File.Exists(path) ? path : throw new FileNotFoundException("orandactl 을 먼저 빌드하세요.", path);
    }

    private sealed class FakeModelLauncher(string baseUrl, string home) : IClaudeProcessLauncher
    {
        private readonly SystemClaudeProcessLauncher _inner = new();

        public IClaudeProcess Start(ClaudeLaunch launch)
        {
            var arguments = launch.Arguments.ToList();
            var at = arguments.IndexOf("--settings") + 1;
            var settings = JsonNode.Parse(arguments[at])!.AsObject();
            settings["modelProvider"] = "gemini";
            arguments[at] = settings.ToJsonString();
            var environment = new Dictionary<string, string?>(launch.Environment ?? new Dictionary<string, string?>())
            {
                ["GEMINI_API_KEY"] = "probe-fake-key",
                ["GOOGLE_GEMINI_BASE_URL"] = baseUrl,
                ["HOME"] = home,
            };
            return _inner.Start(launch with { Arguments = arguments, Environment = environment });
        }
    }

    /// <summary>
    /// 도구 호출이 도착하면 취소될 때까지 끝나지 않는 대체 게이트. 첫 호출 도착을 0 으로 두고 ① 브리지 <c>ct</c> 취소 시각 ② 세션 폐기 토큰(실제
    /// <c>AgentGate</c> 가 <c>ct</c> 와 묶어 처리자에 넘기는 값) 취소 시각 ③ 호출별 도착 시각을 기록한다.
    /// </summary>
    private sealed class HangingGate : IAgentGate
    {
        private readonly Stopwatch _clock = new();
        private readonly object _sync = new();
        private readonly List<TimeSpan> _entries = [];
        private TimeSpan? _bridgeCancel;
        private TimeSpan? _handlerCancel;
        private TimeSpan? _frozenBridge;
        private TimeSpan? _frozenHandler;

        public bool Entered => Entries.Count > 0;

        public IReadOnlyList<TimeSpan> Entries
        {
            get
            {
                lock (_sync)
                {
                    return [.. _entries];
                }
            }
        }

        /// <summary>실행이 끝나고 호스트를 유지한 채 기다린 뒤(<see cref="Freeze"/>) 본 브리지 <c>ct</c> 취소 시각(첫 도착 기준). 취소되지 않았으면 null.</summary>
        public TimeSpan? CancelledAfter
        {
            get
            {
                lock (_sync)
                {
                    return _frozenBridge;
                }
            }
        }

        /// <summary>같은 시점에 본 세션 폐기 토큰 취소 시각(첫 도착 기준). 취소되지 않았으면 null.</summary>
        public TimeSpan? HandlerCancelledAfter
        {
            get
            {
                lock (_sync)
                {
                    return _frozenHandler;
                }
            }
        }

        /// <summary>호스트를 해제하기 전의 상태를 붙잡는다. 호스트 해제는 연결 토큰을 취소하므로 그 뒤의 값은 쓰지 않는다.</summary>
        public void Freeze()
        {
            lock (_sync)
            {
                _frozenBridge = _bridgeCancel;
                _frozenHandler = _handlerCancel;
            }
        }

        public string EntriesText() => string.Join(", ", Entries.Select(t => t.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

        public TaskCompletionSource EnteredSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) => [.. AgentTools.All.Select(AgentToolInfo.From)];

        public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            lock (_sync)
            {
                if (!_clock.IsRunning)
                {
                    _clock.Start();
                    session.RevocationToken.Register(() =>
                    {
                        lock (_sync)
                        {
                            _handlerCancel ??= _clock.Elapsed;
                        }
                    });
                }

                _entries.Add(_clock.Elapsed);
            }

            EnteredSignal.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                lock (_sync)
                {
                    _bridgeCancel ??= _clock.Elapsed;
                }

                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }
}
