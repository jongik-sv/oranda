using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.Tuning;
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

namespace Oranda.App.Tests.Features.Ai;

/// <summary>
/// opencode 드라이버의 실제 왕복(D-037 「켜는 절차」 확인용). 실제 <c>OpencodeDriver</c> · 실제 <c>TuningResult</c> 스키마 · 실제 튜닝 요청 글(<c>AiTuningFlow.BuildPrompt</c>)로 돌고,
/// 결과를 <c>AiTuningFlow.Capture</c>(비공개, 리플렉션)로 붙잡아 <c>TuningResult</c> 로 읽는다. 위치가 App.Tests 인 이유: Capture 와 BuildPrompt 가 App 쪽이다.
/// 실제 <c>AgentBridgeHost</c> 에 실제 플러그인이 붙어 도구 호출이 지난다. 대체한 것: 게이트(<c>AgentGate</c>)는 <c>RecordingGate</c> 로 바꿨다
/// (실제 <c>AgentTools</c> 도구 목록을 내고 예시 결과를 돌려주며 판단 사건을 낸다). DB 접속도 컨테이너도 쓰지 않는다.
/// 실제 모델은 사용자의 opencode 기본 설정(공급자·기본 모델)을 그대로 쓴다.
/// <para>
/// 확인하는 것: (1) 도구 호출 1건 이상 성공 (2) 최종 결과가 <c>AiTuningFlow.Capture</c> 로 파싱 (3) 스트림 감시가 정상 실행을 닫지 않음 (4) 잠금: 토큰이 opencode 데이터 폴더의 어떤 파일에도 없음,
/// 실행 중 <c>opencode</c> 밖의 자식 프로세스가 없음, 실행 뒤 남은 프로세스가 없음, 부른 도구가 <c>AgentTools</c> 안.
/// 관찰한 값(자식 프로세스 목록, 새로 생기거나 바뀐 데이터 폴더 파일)은 시험 출력에 남긴다.
/// </para>
/// 실행: <c>ORANDA_AI_E2E_OPENCODE_REAL=1 dotnet test tests/Oranda.App.Tests -c Debug --filter FullyQualifiedName~AiSystemCliOpencodeRoundTripTests</c>
/// </summary>
public sealed class AiSystemCliOpencodeRoundTripTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] XdgVariables = ["XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_CONFIG_HOME"];

    private static readonly string[] WatchedFolders = [".local/share/opencode", ".local/state/opencode", ".cache/opencode", ".config/opencode"];

    private const string SampleStatement = "SELECT e.empno, e.ename FROM emp e WHERE e.deptno = 10 ORDER BY e.ename";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-opencode-rt-" + Guid.NewGuid().ToString("N"));

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
        catch (UnauthorizedAccessException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [OpencodeRoundTripFact("ORANDA_AI_E2E_OPENCODE_REAL")]
    public async Task Real_opencode_calls_an_agent_tool_and_returns_a_structured_result_that_the_tuning_flow_captures()
    {
        Directory.CreateDirectory(_dir);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var watched = WatchedFolders
            .Select(relative => Path.Combine(home, relative))
            .Concat(XdgVariables
                .Select(Environment.GetEnvironmentVariable)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.Combine(value!, "opencode")))
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var started = DateTime.UtcNow.AddSeconds(-1);
        var baseline = ProcessSnapshot.Take();

        var tokens = new RecordingTokens();
        var gate = new RecordingGate();
        var unique = Guid.NewGuid().ToString("N");
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oa-{unique[..8]}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();
        var driver = new OpencodeDriver(
            tokens,
            gate,
            host,
            new AppDataPaths(_dir),
            new ModelArgsLauncher(Environment.GetEnvironmentVariable("ORANDA_AI_E2E_OPENCODE_MODEL")),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            path => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null,
            Environment.GetEnvironmentVariable,
            home,
            TimeSpan.FromMinutes(6),
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, OpencodeDriver.LaunchEnvironment(null), ct));

        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("dev-example", null, AiAccessLevel.Plan);
        var request = new AiRequest(
            AiTuningFlow.BuildPrompt(new TuningStartChoice(Benchmark: false, IndexDdl: true, ModelId: "default"), SampleStatement),
            context,
            OutputSchema: TuningResult.Schema(),
            Session: new AgentSession(new AgentRunScope("e2e-opencode-structured", profile, context)));

        // 실제 앱처럼 AiTuningFlow.Capture 를 거친다(비공개 정적 메서드).
        var capture = typeof(AiTuningFlow).GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static)!;
        JsonElement? captured = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var stream = (IAsyncEnumerable<AiEvent>)capture.Invoke(null, [driver.RunAsync(request, timeout.Token), (Action<JsonElement?>)(r => captured = r), CancellationToken.None])!;

        // 실행 중 새 프로세스 트리를 0.25초마다 본다(opencode run 이 낳은 자식).
        using var sampling = new CancellationTokenSource();
        var observed = new SortedSet<string>(StringComparer.Ordinal);
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                foreach (var command in ProcessSnapshot.Take().NewDescendantsOfLockedRun(baseline))
                {
                    observed.Add(command);
                }

                try
                {
                    await Task.Delay(250, sampling.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var events = new List<AiEvent>();
        try
        {
            await foreach (var e in stream.WithCancellation(timeout.Token))
            {
                events.Add(e);
            }
        }
        finally
        {
            await sampling.CancelAsync();
            await sampler;
        }

        foreach (var e in events)
        {
            output.WriteLine(Describe(e));
        }

        // 스트림 감시가 정상 실행을 닫지 않았다.
        Assert.DoesNotContain(events, e => e is AiError);
        Assert.IsType<AiFinal>(events[^1]);
        Assert.NotNull(captured);
        Assert.NotEqual(JsonValueKind.Null, captured!.Value.ValueKind);
        var result = TuningResult.Parse(captured.Value);
        Assert.NotEmpty(result.Summary);

        // 도구 호출이 1건 이상 성공했다: 화면 사건과 게이트 쪽 기록이 함께 증명한다. 게이트 판단이 없는 호출은 성공이 아니다.
        var agentToolNames = AgentTools.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(events, e => e is AiToolCall call && agentToolNames.Contains(call.ToolName));
        Assert.Contains(events, e => e is AiToolResult { Judgement: AiToolJudgement.Allowed or AiToolJudgement.Masked });
        Assert.True(gate.Calls.Count >= 1, "게이트에 도구 호출이 한 건도 도착하지 않았다.");
        Assert.All(gate.Calls, call => Assert.Contains(call, agentToolNames));
        Assert.All(events.OfType<AiToolCall>(), call => Assert.Contains(call.ToolName, agentToolNames));
        output.WriteLine("게이트에 도착한 호출: " + string.Join(", ", gate.Calls));

        // 잠금 쪽 1: 토큰 폐기.
        var token = Assert.Single(tokens.Issued);
        Assert.Equal(tokens.Issued, tokens.Revoked);

        // 잠금 쪽 2: 실행 중 본 자식 프로세스는 모두 opencode 자기 것이다(셸·다른 도구 없음).
        output.WriteLine("실행 중 본 새 프로세스: " + (observed.Count == 0 ? "(없음)" : string.Join(" || ", observed)));
        Assert.NotEmpty(observed);
        Assert.All(observed, command => Assert.Equal("opencode", command.Split(' ')[0])); // 실행 파일 이름이 opencode 인 것만(셸·다른 도구 없음)

        // 잠금 쪽 3: 실행이 끝나고 잠시 뒤 남은 새 opencode 프로세스가 없다.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var leftover = ProcessSnapshot.Take().NewOpencodeProcesses(baseline);
        output.WriteLine("실행 뒤 남은 새 opencode 프로세스: " + (leftover.Count == 0 ? "(없음)" : string.Join(" || ", leftover)));
        Assert.Empty(leftover);

        // 잠금 쪽 4: 토큰이 opencode 가 쓰는 폴더의 어떤 파일에도 남지 않았다(이번 실행 중 바뀌었거나 새로 생긴 파일만 본다).
        var scanned = 0;
        var changedByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in watched.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (info.LastWriteTimeUtc < started || info.Length > 600_000_000)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            var area = watched.First(d => file.StartsWith(d, StringComparison.Ordinal));
            var kind = Path.GetRelativePath(area, file).Split(Path.DirectorySeparatorChar)[0]; // 하위 폴더 종류만 남긴다(저장소 이름 등이 나오지 않게)
            changedByKind[Path.GetFileName(area) + ":" + (kind.Contains('.', StringComparison.Ordinal) ? "(file)" : kind)] = changedByKind.GetValueOrDefault(Path.GetFileName(area) + ":" + (kind.Contains('.', StringComparison.Ordinal) ? "(file)" : kind)) + 1;
            Assert.False(await FileContainsAsync(file, Encoding.UTF8.GetBytes(token)), "토큰이 opencode 폴더의 파일에 남았다.");
        }

        output.WriteLine($"토큰 검사한 파일 수: {scanned} ({string.Join(", ", changedByKind.Select(kv => kv.Key + "=" + kv.Value))})");

        // 드라이버가 쓴 실행 폴더는 실행이 끝나면 지워진다.
        Assert.Empty(Directory.EnumerateFiles(_dir, OpencodePluginFile.RelativePath.Split(Path.DirectorySeparatorChar)[^1], SearchOption.AllDirectories));
    }

    private static string Describe(AiEvent e) => e switch
    {
        AiToolCall call => $"도구 호출: {call.ToolName}",
        AiToolResult result => $"도구 결과: {result.ToolName} {result.Judgement}",
        AiError error => $"오류: {error.Code} {error.Message}",
        AiFinal => "최종 결과",
        AiStarted started => $"시작: {started.ResumeId}",
        _ => e.GetType().Name,
    };

    /// <summary>파일에 바이트열이 있는지 본다(큰 파일도 덩어리로 읽으며, 덩어리 경계에 걸친 경우까지 본다).</summary>
    private static async Task<bool> FileContainsAsync(string path, byte[] needle)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[1 << 20];
        var carry = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry));
            if (read == 0)
            {
                return false;
            }

            var total = carry + read;
            if (buffer.AsSpan(0, total).IndexOf(needle) >= 0)
            {
                return true;
            }

            carry = Math.Min(needle.Length - 1, total);
            buffer.AsSpan(total - carry, carry).CopyTo(buffer);
        }
    }

    /// <summary><c>ps</c> 한 번의 결과(pid, 부모 pid, 명령줄).</summary>
    private sealed class ProcessSnapshot
    {
        private readonly List<(int Pid, int Parent, string Command)> _rows;

        private ProcessSnapshot(List<(int Pid, int Parent, string Command)> rows) => _rows = rows;

        public static ProcessSnapshot Take()
        {
            var info = new ProcessStartInfo("ps", ["-A", "-o", "pid=,ppid=,command="]) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(info)!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var rows = new List<(int, int, string)>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
                {
                    rows.Add((pid, parent, parts[2].Trim()));
                }
            }

            return new ProcessSnapshot(rows);
        }

        public bool Contains(int pid) => _rows.Exists(r => r.Pid == pid);

        /// <summary>처음에는 없던 opencode 프로세스의 명령줄.</summary>
        public List<string> NewOpencodeProcesses(ProcessSnapshot baseline)
            => [.. _rows.Where(r => !baseline.Contains(r.Pid) && IsOpencode(r.Command)).Select(r => Trim(r.Command))];

        /// <summary>처음에는 없던 <c>opencode run … --agent oranda_locked</c> 와 그 자손 전부의 명령줄(뿌리 포함).</summary>
        public List<string> NewDescendantsOfLockedRun(ProcessSnapshot baseline)
        {
            var roots = _rows.Where(r => !baseline.Contains(r.Pid) && r.Command.Contains(" run ", StringComparison.Ordinal) && r.Command.Contains("oranda_locked", StringComparison.Ordinal)).Select(r => r.Pid).ToHashSet();
            var all = new HashSet<int>(roots);
            bool grew;
            do
            {
                grew = false;
                foreach (var row in _rows)
                {
                    if (all.Contains(row.Parent) && all.Add(row.Pid))
                    {
                        grew = true;
                    }
                }
            }
            while (grew);

            return [.. _rows.Where(r => all.Contains(r.Pid)).Select(r => Trim(r.Command))];
        }

        private static bool IsOpencode(string command) => command.Contains("/opencode", StringComparison.Ordinal) || command.StartsWith("opencode", StringComparison.Ordinal);

        /// <summary>
        /// 시험 출력에 남기는 명령줄: 실행 파일 이름과 처음 몇 낱말만 남기고 <c>-m</c> 다음 값(공급자/모델)은 가린다.
        /// 판정(<c>opencode</c> 로 시작)도 이 값으로 한다: 경로나 인자 어디에 opencode 가 들어 있는지는 보지 않는다.
        /// </summary>
        private static string Trim(string command)
        {
            var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string> { Path.GetFileName(words[0]) };
            for (var i = 1; i < words.Length && kept.Count < 8; i++)
            {
                kept.Add(words[i - 1] == "-m" ? "<model>" : words[i]);
            }

            return string.Join(' ', kept);
        }
    }

    /// <summary>
    /// 사용 중인 공급자·모델 지정을 <c>run</c> 바로 뒤에 끼워 넣는 실행기(잠금 인자는 건드리지 않는다). 드라이버는 모델을 지정하지 않으므로 사용자의 기본 모델이
    /// 무료 한도 같은 사정으로 막혀 있으면 시험이 모델 호출 단계에서 막힌다: <c>ORANDA_AI_E2E_OPENCODE_MODEL</c>(<c>공급자/모델</c>)로 고른다. 비어 있으면 기본 모델 그대로.
    /// 값은 저장소에 두지 않는다.
    /// </summary>
    private sealed class ModelArgsLauncher(string? model) : IClaudeProcessLauncher
    {
        private readonly SystemClaudeProcessLauncher _inner = new();

        public IClaudeProcess Start(ClaudeLaunch launch)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return _inner.Start(launch);
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._:/#-]*\z"))
            {
                throw new InvalidOperationException("ORANDA_AI_E2E_OPENCODE_MODEL 은 공급자/모델 형식이어야 한다.");
            }

            var arguments = launch.Arguments.ToList();
            arguments.InsertRange(arguments.IndexOf("run") + 1, ["-m", model]);
            return _inner.Start(launch with { Arguments = arguments });
        }
    }

    private sealed class RecordingTokens : IAgentRunTokens
    {
        private readonly AgentRunTokenStore _inner = new();

        public List<string> Issued { get; } = [];

        public List<string> Revoked { get; } = [];

        public string Issue(AgentSession session)
        {
            var token = _inner.Issue(session);
            Issued.Add(token);
            return token;
        }

        public AgentSession? Resolve(string token) => _inner.Resolve(token);

        public void Revoke(string token)
        {
            Revoked.Add(token);
            _inner.Revoke(token);
        }
    }

    /// <summary>실제 <see cref="AgentTools"/> 도구 목록을 제공하고 호출을 기록하며, 게이트처럼 판단 사건(<c>ToolCompleted</c>)을 내는 대체 게이트(감사 기록·접속 없음).</summary>
    private sealed class RecordingGate : IAgentGate
    {
        private readonly List<string> _calls = [];

        public List<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) => [.. AgentTools.All.Select(AgentToolInfo.From)];

        public Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            lock (_calls)
            {
                _calls.Add(toolName);
            }

            var text = toolName == AgentToolNames.DescribeObject
                ? """{"object":"EMP","num_rows":1234,"columns":["EMPNO","ENAME","DEPTNO"]}"""
                : """{"note":"example result"}""";
            ToolCompleted?.Invoke(this, new AgentToolActivityEventArgs(session.RunId, session.NextSequence(), toolName, AiToolJudgement.Allowed, text.Length, null));
            return Task.FromResult(new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed));
        }
    }
}

/// <summary>지정한 환경 변수가 <c>1</c> 이 아니면 건너뛴다(실제 모델을 부르는 왕복은 명시한 때만 돈다).</summary>
public sealed class OpencodeRoundTripFactAttribute : FactAttribute
{
    public OpencodeRoundTripFactAttribute(string environmentVariable)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) != "1")
        {
            Skip = $"{environmentVariable}=1 일 때만 돈다.";
        }
    }
}
