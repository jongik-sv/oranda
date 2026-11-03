using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 지금 설치된 agy 가 잠금 인자로 내보내는 모델 요청의 도구 목록과, 강제로 부른 내장 도구의 결과를 확인한다.
/// agy 는 자동 업데이트되고 잠금이 <c>excludeDefaultComponents</c> 의 의미에 기대므로, 업데이트로 잠금이 깨지는 것을 잡는 유일한 수단이다.
/// 환경 변수 <c>ORANDA_AI_E2E_AGY=1</c> 일 때만 돈다. 가짜 모델 서버(<c>GEMINI_API_KEY</c> 경로)만 쓰므로 실제 로그인·모델 호출은 없다.
/// </summary>
public sealed class AgyEndToEndTests : IDisposable
{
    /// <summary>잠금 상태에서 모델에 선언되는 도구의 허용 목록(탐침 결과). 이 밖의 선언은 잠금이 깨진 것이다.</summary>
    private static readonly string[] AllowedDeclarations = ["finish", "call_mcp_tool", "list_resources", "read_resource", "manage_task"];

    private static readonly string[] ForcedBuiltins =
    [
        "run_command", "read_file", "view_file", "write_file", "write_to_file", "edit_file", "sed_file", "grep_search", "list_dir",
        "find_by_name", "search_web", "read_url_content", "open_browser_url", "execute_browser_javascript", "browser_subagent",
        "invoke_subagent", "define_subagent", "manage_subagents", "schedule", "send_message", "generate_image", "run_workflow", "ask_question",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-agy-e2e-" + Guid.NewGuid().ToString("N"));

    public AgyEndToEndTests() => Directory.CreateDirectory(_dir);

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

    [AgyEndToEndFact]
    public async Task Locked_agy_declares_only_the_allow_listed_tools_to_the_model()
    {
        var run = await RunAgyAsync(calls: []);

        Assert.NotEmpty(run.DeclaredTools);
        Assert.All(run.DeclaredTools, tool => Assert.Contains(tool, AllowedDeclarations));
    }

    [AgyEndToEndFact]
    public async Task Forced_builtin_tool_calls_are_all_unknown_tool_and_leave_no_file_behind()
    {
        var marker = Path.Combine(_dir, "run", "created-by-model.txt");
        var calls = ForcedBuiltins.Select(name => (name, args: ArgumentsFor(name, marker))).ToList();

        var run = await RunAgyAsync(calls);

        Assert.Equal(calls.Count, run.ToolResponses.Count);
        Assert.All(run.ToolResponses.Zip(calls), pair => Assert.Contains("unknown tool", pair.First, StringComparison.Ordinal));
        Assert.False(File.Exists(marker));
    }

    [AgyEndToEndFact]
    public async Task An_orandactl_mcp_call_is_the_only_tool_that_runs()
    {
        var call = ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "echo_probe", ["Arguments"] = new JsonObject { ["text"] = "hi" } });

        var run = await RunAgyAsync([call]);

        var response = Assert.Single(run.ToolResponses);
        Assert.Contains("echo:hi", response, StringComparison.Ordinal);
        var events = run.Stream.SelectMany(AgyStreamParser.Parse).ToList();
        Assert.DoesNotContain(events, e => e is AgyUnknownStep); // 지금 agy 가 내는 사건은 모두 파서가 아는 형식이다
        var steps = events.OfType<AgyToolStep>().ToList();
        Assert.Contains(steps, s => s.State == "ACTIVE" && s.Server == "orandactl" && s.Tool == "echo_probe");
    }

    [AgyEndToEndFact]
    public async Task The_run_token_reaches_the_orandactl_server_through_the_environment_and_leaves_no_trace_in_requests_arguments_files_or_the_agent_file()
    {
        const string Prefix = "fake-run-token-";
        var token = Prefix + Guid.NewGuid().ToString("N");
        var call = ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "echo_probe", ["Arguments"] = new JsonObject { ["text"] = "hi" } });

        var run = await RunAgyAsync([call], token);

        // 1. orandactl 자리의 서버가 환경 변수로 토큰을 받는다(응답에는 해시만 돌아온다). 이 실행에서 아래 검사를 한다.
        var response = Assert.Single(run.ToolResponses);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.True(response.Contains("tokenSha256=" + expectedHash, StringComparison.Ordinal), "orandactl 자리의 서버가 환경 변수의 토큰을 받지 못했다.");

        // 2. 명령줄 인자와 agy 의 표준 출력 스트림에 토큰이 없다.
        Assert.True(!run.Arguments.Any(a => a.Contains(token, StringComparison.Ordinal)), "명령줄 인자에 토큰이 있다.");
        Assert.True(!run.Stream.Any(line => line.Contains(token, StringComparison.Ordinal)), "표준 출력 스트림에 토큰이 있다.");

        // 3. 가짜 모델 서버가 받은 요청 본문 전체에 토큰이 없다.
        Assert.NotEmpty(run.RequestBodies);
        var leakedBodies = run.RequestBodies.Count(body => body.Contains(token, StringComparison.Ordinal));
        Assert.True(leakedBodies == 0, $"모델 서버가 받은 요청 {leakedBodies}건의 본문에 토큰이 있다.");

        // 4. 빈 통과 방지: 검사 대상 파일이 실제로 생겼고 비어 있지 않다. 생기지 않는 위치는 그 사실을 고정한다.
        var gemini = Path.Combine(run.DecoyHome, ".gemini", "antigravity-cli");
        var databases = Directory.GetFiles(Path.Combine(gemini, "conversations"), "*.db");
        Assert.Single(databases);
        Assert.All(databases, file => Assert.True(new FileInfo(file).Length > 0, "대화 기록 DB 가 비어 있다: " + Path.GetFileName(file)));
        Assert.Empty(Directory.GetFiles(Path.Combine(gemini, "conversations"), "*.db-*")); // -wal·-shm 은 생기지 않는다
        var transcripts = Directory.GetFiles(Path.Combine(gemini, "brain"), "transcript*.jsonl", SearchOption.AllDirectories);
        Assert.NotEmpty(transcripts);
        Assert.All(transcripts, file => Assert.True(new FileInfo(file).Length > 0, "brain 기록이 비어 있다: " + Path.GetFileName(file)));
        var logs = Directory.GetFiles(Path.Combine(gemini, "log"), "cli-*.log");
        Assert.NotEmpty(logs);
        Assert.All(logs, file => Assert.True(new FileInfo(file).Length > 0, "로그가 비어 있다: " + Path.GetFileName(file)));
        Assert.Empty(Directory.GetFiles(run.TempDirectory, "*", SearchOption.AllDirectories)); // TMPDIR 아래에는 아무것도 남지 않는다

        // 5. 미끼 HOME 전체·TMPDIR·에이전트 파일에 토큰이 없다(있으면 값 대신 파일 이름만 보고한다).
        var files = Directory.EnumerateFiles(run.DecoyHome, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(run.TempDirectory, "*", SearchOption.AllDirectories))
            .Append(run.AgentFile);
        var leaked = files
            .Where(path => File.ReadAllText(path, Encoding.Latin1).Contains(token, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(_dir, path))
            .ToList();
        Assert.True(leaked.Count == 0, "토큰이 든 파일: " + string.Join(", ", leaked));
    }

    /// <summary>
    /// 사용자 전역 설정에 둔 미끼 MCP 서버가 잠금 상태에서도 뜨는 위치를 측정해, 그 위치를 드라이버가 모두 거부하는지 확인한다.
    /// 뜨는 위치 집합이 측정값(agy 1.4.12)과 정확히 같아야 한다: agy 업데이트로 새 위치에서 뜨면(거부가 뚫림) 또는 사라지면(재측정 필요) 깨진다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task Decoy_mcp_servers_start_only_at_the_measured_locations_and_the_driver_refuses_every_one_of_them()
    {
        const string PluginManifest = """{"name":"p1","version":"1.0.0","description":"decoy"}""";
        var cases = new (string Name, string Relative, bool Plugin, bool GeminiDirVariable)[]
        {
            ("config", ".gemini/config/mcp_config.json", false, false),
            ("antigravity", ".gemini/antigravity/mcp_config.json", false, false),
            ("antigravity-cli", ".gemini/antigravity-cli/mcp_config.json", false, false),
            ("gemini-root", ".gemini/mcp_config.json", false, false),
            ("env-gemini-dir", "altgem/config/mcp_config.json", false, true),
            ("plugin-config", ".gemini/config/plugins/p1/mcp_config.json", true, false),
            ("plugin-antigravity", ".gemini/antigravity/plugins/p1/mcp_config.json", true, false),
            ("plugin-cli", ".gemini/antigravity-cli/plugins/p1/mcp_config.json", true, false),
            ("plugin-ide", ".gemini/antigravity-ide/plugins/p1/mcp_config.json", true, false),
            ("plugin-root", ".gemini/plugins/p1/mcp_config.json", true, false),
            ("plugin-skills", ".gemini/config/skills/plugins/p1/mcp_config.json", true, false),
        };
        var started = new List<string>();
        var refused = new List<string>();
        foreach (var (name, relative, plugin, geminiDirVariable) in cases)
        {
            using var experiment = new AgyEndToEndTests();
            var environment = geminiDirVariable
                ? new Dictionary<string, string> { ["ANTIGRAVITY_GEMINI_DIR"] = Path.Combine(experiment._dir, "home", "altgem") }
                : null;
            var capturedName = name;
            var capturedRelative = relative;
            var capturedPlugin = plugin;

            var run = await experiment.RunAgyAsync(
                [],
                token: null,
                environment,
                async context =>
                {
                    await DecoyContext.WriteAsync(Path.Combine(context.Home, capturedRelative), context.McpConfig(capturedName));
                    if (capturedPlugin)
                    {
                        await DecoyContext.WriteAsync(Path.Combine(context.Home, capturedRelative[..capturedRelative.LastIndexOf('/')], "plugin.json"), JsonNode.Parse(PluginManifest)!);
                    }
                });

            if (File.Exists(run.DecoyLog))
            {
                started.Add(name);
                var driver = new AgyDriver(
                    new FakeRunTokens(), new FakeGate(), new FixedEndpoint("pipe"), new Oranda.Platform.Storage.AppDataPaths(experiment._dir),
                    new FakeLauncher(), "orandactl", () => null, _ => false, run.DecoyHome, TimeSpan.FromMinutes(1), (_, _, _) => Task.FromResult<string?>("1.4.12"));
                if (driver.GlobalMcpConfigWithServers() is not null)
                {
                    refused.Add(name);
                }
            }
        }

        Assert.Equal(["config", "plugin-config", "plugin-cli"], started);
        Assert.Equal(started, refused);
    }

    /// <summary>
    /// 상위 폴더 탐색(.agents 등)과 플러그인 훅이 잠금을 지나는지 측정해 고정한다. 잠금 실행에서는 어느 미끼도 뜨지 않아야 하고,
    /// 같은 배치를 잠금 없이 돌린 대조군은 모두 떠야 한다(대조군이 뜨지 않으면 미끼 형식이 틀려 잠금 시험이 비어 버린다).
    /// 실행 폴더는 앱처럼 HOME 아래 앱 데이터 경로에 두고, 상위에 실제 저장소(git init)가 있는 경우와 없는 경우를 본다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task Parent_folder_customizations_and_plugin_hooks_do_not_start_under_the_lock_while_the_unlocked_control_starts_them()
    {
        var appData = Path.Combine("Library", "Application Support", "Oranda");
        var locations = new Dictionary<string, string>
        {
            ["run"] = Path.Combine(appData, "ai", "runs", "run-1"),
            ["runs"] = Path.Combine(appData, "ai", "runs"),
            ["home"] = string.Empty,
        };
        var kinds = new[] { "plugin-mcp", "root-mcp", "plugins-json", "hooks" };
        var lockedCases = new List<(string Name, string Location, string Kind, string Git)>();
        foreach (var kind in kinds)
        {
            lockedCases.Add((".agents", "run", kind, "none")); // 실행 폴더 자신(작업 폴더)
            lockedCases.Add((".agents", "runs", kind, "appdata")); // 실행 폴더의 상위 + 그 위 저장소
            lockedCases.Add((".agents", "home", kind, "home")); // HOME + HOME 이 저장소
            lockedCases.Add((".agents", "runs", kind, "none")); // 저장소 없음
        }

        foreach (var name in new[] { ".agent", "_agents", "_agent" })
        {
            lockedCases.Add((name, "home", "plugin-mcp", "home"));
        }

        var started = new List<string>();
        foreach (var (name, location, kind, git) in lockedCases)
        {
            if (await StartedAsync(name, location, kind, git, locked: true))
            {
                started.Add($"{name}|{location}|{kind}|git@{git}");
            }
        }

        Assert.True(started.Count == 0, "잠금 상태에서 미끼가 떴다: " + string.Join(", ", started));

        // 대조군: 잠금 없이는 같은 배치에서 뜬다(저장소가 있으면 상위 폴더에서도).
        var notStarted = new List<string>();
        var controls = new List<(string Name, string Location, string Kind, string Git)>();
        foreach (var kind in kinds)
        {
            controls.Add((".agents", "run", kind, "none"));
            controls.Add((".agents", "runs", kind, "appdata"));
            controls.Add((".agents", "home", kind, "home"));
        }

        foreach (var name in new[] { ".agent", "_agents", "_agent" })
        {
            controls.Add((name, "run", "plugin-mcp", "none"));
        }

        foreach (var (name, location, kind, git) in controls)
        {
            if (!await StartedAsync(name, location, kind, git, locked: false))
            {
                notStarted.Add($"{name}|{location}|{kind}|git@{git}");
            }
        }

        Assert.True(notStarted.Count == 0, "대조군이 뜨지 않았다(미끼 형식이나 탐색 규칙을 다시 확인): " + string.Join(", ", notStarted));

        async Task<bool> StartedAsync(string name, string location, string kind, string git, bool locked)
        {
            const string Manifest = """{"name":"p1","version":"1.0.0","description":"decoy"}""";
            using var experiment = new AgyEndToEndTests();
            var label = $"{name}|{location}|{kind}|git@{git}";
            var run = await experiment.RunAgyAsync(
                [],
                token: "fake-run-token-" + Guid.NewGuid().ToString("N"),
                extraEnvironment: null,
                arrange: async context =>
                {
                    var root = Path.Combine(context.Home, locations[location], name);
                    switch (kind)
                    {
                        case "plugin-mcp":
                            await DecoyContext.WriteAsync(Path.Combine(root, "plugins", "p1", "plugin.json"), JsonNode.Parse(Manifest)!);
                            await DecoyContext.WriteAsync(Path.Combine(root, "plugins", "p1", "mcp_config.json"), context.McpConfig(label));
                            break;
                        case "root-mcp":
                            await DecoyContext.WriteAsync(Path.Combine(root, "mcp_config.json"), context.McpConfig(label));
                            break;
                        case "plugins-json":
                            var shared = Path.Combine(Path.GetDirectoryName(context.Log)!, "shared");
                            await DecoyContext.WriteAsync(Path.Combine(shared, "p1", "plugin.json"), JsonNode.Parse(Manifest)!);
                            await DecoyContext.WriteAsync(Path.Combine(shared, "p1", "mcp_config.json"), context.McpConfig(label));
                            await DecoyContext.WriteAsync(Path.Combine(root, "plugins.json"), new JsonObject { ["entries"] = new JsonArray(new JsonObject { ["path"] = shared }) });
                            break;
                        default:
                            await DecoyContext.WriteAsync(Path.Combine(root, "hooks.json"), context.HooksConfig(label));
                            break;
                    }

                    if (git != "none")
                    {
                        var repository = Path.Combine(context.Home, git == "home" ? string.Empty : appData);
                        Directory.CreateDirectory(repository);
                        using var init = Process.Start(new ProcessStartInfo("git", ["init", "-q", repository]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                        await init.WaitForExitAsync();
                    }
                },
                runRelativeToHome: locations["run"],
                locked: locked);
            return File.Exists(run.DecoyLog);
        }
    }

    /// <summary>
    /// 플러그인의 훅(<c>hooks.json</c>)과 전역 훅이 잠금을 지나는지 측정한다. 훅은 agy 의 자식 프로세스로 실행되어 토큰 환경 변수를 물려받으므로
    /// 잠금 실행에서는 뜨지 않아야 하고, 잠금 없는 대조군은 떠야 한다(대조군의 환경에는 토큰 변수가 있다).
    /// </summary>
    [AgyEndToEndFact]
    public async Task Plugin_and_global_hooks_do_not_start_under_the_lock_while_the_unlocked_control_starts_them_with_the_token_variable()
    {
        const string Manifest = """{"name":"p1","version":"1.0.0","description":"decoy"}""";
        var places = new[] { ("plugin-config", ".gemini/config/plugins/p1/hooks.json"), ("plugin-cli", ".gemini/antigravity-cli/plugins/p1/hooks.json"), ("global", ".gemini/config/hooks.json") };
        foreach (var (name, relative) in places)
        {
            foreach (var locked in new[] { true, false })
            {
                using var experiment = new AgyEndToEndTests();
                var run = await experiment.RunAgyAsync(
                    [],
                    token: "fake-run-token-" + Guid.NewGuid().ToString("N"),
                    extraEnvironment: null,
                    arrange: async context =>
                    {
                        if (relative.Contains("plugins/", StringComparison.Ordinal))
                        {
                            await DecoyContext.WriteAsync(Path.Combine(context.Home, relative[..relative.LastIndexOf('/')], "plugin.json"), JsonNode.Parse(Manifest)!);
                        }

                        await DecoyContext.WriteAsync(Path.Combine(context.Home, relative), context.HooksConfig(name));
                    },
                    locked: locked);

                var records = File.Exists(run.DecoyLog) ? File.ReadAllLines(run.DecoyLog) : [];
                if (locked)
                {
                    Assert.True(records.Length == 0, $"잠금 상태에서 훅이 실행됐다: {name}");
                }
                else
                {
                    Assert.True(records.Length > 0, $"대조군 훅이 실행되지 않았다(훅 형식이나 신뢰 조건을 확인): {name}");
                    Assert.All(records, line => Assert.Contains("\"hasTokenVariable\":true", line, StringComparison.Ordinal));
                }
            }
        }
    }

    /// <summary>
    /// 로그인 셸(rc 파일)과 자식 프로세스를 측정한다. 미끼 HOME 의 rc 파일이 표지를 남기게 하고 SHELL 을 맞춘다.
    /// 잠금 실행에서는 어느 rc 파일도 실행되지 않아야 하고, 명령 실행을 허용한 대조군(잠금 없음)에서는 실행되어야 한다(표지가 동작한다는 증거).
    /// </summary>
    [AgyEndToEndFact]
    public async Task Login_shell_rc_files_do_not_run_under_the_lock_while_the_control_command_runs_them()
    {
        var rcFiles = new[] { ".zshenv", ".zprofile", ".zshrc", ".zlogin", ".bash_profile", ".bashrc", ".profile" };
        foreach (var shell in new[] { "/bin/zsh", "/bin/bash" })
        {
            foreach (var control in new[] { false, true })
            {
                using var experiment = new AgyEndToEndTests();
                var commandMarker = Path.Combine(experiment._dir, "run", "command-ran"); // 명령 샌드박스는 작업 폴더(실행 폴더) 밖에 쓰지 못할 수 있다
                var rcLog = Path.Combine(experiment._dir, "run", "rc.log");
                var calls = control ? new[] { ("run_command", new JsonObject { ["CommandLine"] = $"touch '{commandMarker}'", ["WaitMsBeforeAsync"] = 3000, ["toolSummary"] = "e" }) } : [];

                var run = await experiment.RunAgyAsync(
                    calls,
                    token: "fake-run-token-" + Guid.NewGuid().ToString("N"),
                    extraEnvironment: null,
                    arrange: async context =>
                    {
                        foreach (var rc in rcFiles)
                        {
                            await File.WriteAllTextAsync(Path.Combine(context.Home, rc), $"echo \"{rc} ${{ORANDA_RUN_TOKEN:+token}}\" >> '{rcLog}'\n");
                        }
                    },
                    locked: !control,
                    allowCommands: control,
                    shell: shell);

                var markers = File.Exists(rcLog) ? File.ReadAllLines(rcLog).Select(m => m.Trim()).Distinct().ToList() : [];
                if (control)
                {
                    // 대조군: 명령이 실제로 돌았고, 표지(rc 파일)가 동작하며, 그 환경에 토큰이 있다. 돌린 셸은 로그인 셸이 아니라 .zshenv 만 읽는다.
                    Assert.True(File.Exists(commandMarker), $"대조군 명령이 실행되지 않았다({shell}).");
                    Assert.Contains(".zshenv token", markers);
                    Assert.DoesNotContain(markers, m => !m.StartsWith(".zshenv", StringComparison.Ordinal));
                }
                else
                {
                    Assert.True(markers.Count == 0, $"잠금 상태에서 rc 파일이 실행됐다({shell}): " + string.Join(", ", markers));
                    Assert.False(File.Exists(commandMarker));
                    // 자식 프로세스: 에이전트가 쓰는 orandactl 자리(sh·node), 백그라운드 갱신기(agy --bg-updater)뿐이고 사용자 셸은 없다.
                    Assert.DoesNotContain(run.ProcessTree, c => c.Contains("zsh", StringComparison.Ordinal) || c.Contains("bash", StringComparison.Ordinal) || c.StartsWith("login", StringComparison.Ordinal));
                }
            }
        }
    }

    /// <summary>
    /// 설치된 agy 가 Oranda 가 잠금을 검증한 버전인지 확인한다. agy 가 올라가면 이 시험이 깨져 재측정을 알린다:
    /// 이 클래스의 시험을 모두 통과하는지 확인한 뒤 <see cref="AgyDriver.VerifiedVersions"/> 에 새 버전을 더한다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task Installed_agy_is_a_version_whose_lock_was_verified()
    {
        var located = SystemCliExecutable.Locate("agy", "agy", Environment.GetEnvironmentVariable("PATH"), File.Exists);
        Assert.True(located.Executable is not null, "agy 실행 파일을 찾지 못했습니다.");

        // 버전 읽기도 드라이버 환경(자동 업데이트 끔)으로 한다. --version 이 갱신기 때문에 기다리지 않는지(5초 상한까지 가지 않는지)도 함께 본다.
        var watch = Stopwatch.StartNew();
        var installed = SystemCliVersion.Parse(await SystemCliVersion.ReadAsync(located.Executable!, TimeSpan.FromSeconds(10), AgyDriver.LaunchEnvironment(null), CancellationToken.None));
        Assert.True(watch.Elapsed < SystemCliVersion.ReadTimeLimit, $"--version 이 {watch.ElapsedMilliseconds}ms 걸렸다: 상한({SystemCliVersion.ReadTimeLimit.TotalSeconds}초)에 가깝다.");
        var notice = await SystemCliVersion.UnverifiedNoticeAsync(
            (executable, limit, ct) => SystemCliVersion.ReadAsync(executable, limit, AgyDriver.LaunchEnvironment(null), ct),
            located.Executable!,
            AgyDriver.VerifiedVersions,
            "Antigravity CLI",
            CancellationToken.None);

        Assert.True(
            notice is null,
            $"설치된 agy 버전({installed ?? "읽지 못함"})은 검증 목록({string.Join(", ", AgyDriver.VerifiedVersions)})에 없다. ORANDA_AI_E2E_AGY=1 시험을 모두 다시 돌려 잠금을 재측정한 뒤 AgyDriver.VerifiedVersions 를 고친다.");
    }

    /// <summary>
    /// 자동 업데이트 끄기(<c>AGY_CLI_DISABLE_AUTO_UPDATE=true</c>)의 효과. 켜 둔 대조군(값 false)에서는 백그라운드 갱신기(<c>agy --bg-updater</c>)가
    /// 뜨고, 드라이버 환경에서는 뜨지 않으며 갱신기 폴더도 만들어지지 않는다. 갱신기가 뜨지 않으면 실행 중 버전이 바뀌는 경로와
    /// 갱신기의 토큰 환경 상속이 함께 사라진다. 값 <c>1</c>·<c>0</c> 은 효과가 없음을 측정했으므로 <c>true</c> 만 쓴다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task The_driver_environment_keeps_the_background_updater_from_starting_while_the_control_starts_it()
    {
        for (var i = 0; i < 3; i++)
        {
            using var control = new AgyEndToEndTests();
            var controlRun = await control.RunAgyAsync([], "fake-run-token-" + Guid.NewGuid().ToString("N"), new Dictionary<string, string> { [AgyDriver.AutoUpdateVariable] = "false" });
            Assert.True(controlRun.ProcessTree.Any(c => c.Contains("--bg-updater", StringComparison.Ordinal)), $"대조군(자동 업데이트 켬)에서 갱신기가 뜨지 않았다(#{i}): 측정이 비어 있다.");

            using var locked = new AgyEndToEndTests();
            var run = await locked.RunAgyAsync([], "fake-run-token-" + Guid.NewGuid().ToString("N"));
            Assert.DoesNotContain(run.ProcessTree, c => c.Contains("--bg-updater", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(run.DecoyHome, ".gemini", "antigravity-cli", "updater")), "갱신기 폴더가 만들어졌다.");
        }
    }

    /// <summary>
    /// 실행이 끝난 뒤(정상 종료, 드라이버가 죽인 경우) agy 자손이 남는지 측정해 고정한다. 갱신기가 뜨는 대조군(자동 업데이트 켬)에서도 호출 측이 프로세스 트리를
    /// 끝낸 경우에는 아무것도 남지 않았다(6회씩 3가지 지연). 정상 종료 때는 드물게(약 35회 중 1회) 갱신기가 2~3초 더 살다 스스로 끝났고 환경에 토큰 변수가 있었다.
    /// 이 시험은 드라이버 환경(갱신기 없음)의 정상 종료와, 갱신기가 있는 대조군에서 죽인 경우를 고정한다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task No_agy_child_outlives_a_normal_run_or_a_killed_run()
    {
        using var normal = new AgyEndToEndTests();
        var normalRun = await normal.RunAgyAsync([], "fake-run-token-" + Guid.NewGuid().ToString("N"));
        Assert.True(normalRun.SurvivingChildren.Count == 0, "정상 종료 뒤 자손이 남았다: " + string.Join("; ", normalRun.SurvivingChildren));

        foreach (var delay in new[] { 1500, 2500, 4000 })
        {
            using var killed = new AgyEndToEndTests();
            var run = await killed.RunAgyAsync(
                [],
                "fake-run-token-" + Guid.NewGuid().ToString("N"),
                new Dictionary<string, string> { [AgyDriver.AutoUpdateVariable] = "false" },
                killAfter: TimeSpan.FromMilliseconds(delay));
            Assert.Contains(run.ProcessTree, c => c.Contains("--bg-updater", StringComparison.Ordinal)); // 대조군: 갱신기가 있었다
            Assert.True(run.SurvivingChildren.Count == 0, $"죽인 뒤({delay}ms) 자손이 남았다: " + string.Join("; ", run.SurvivingChildren));
        }
    }

    /// <summary>
    /// 구조 결과(스키마) 실행의 끝을 실제 agy 로 확인한다: 마지막에 <c>finish</c> 도구 단계와 <c>step_type: "finish"</c> 단계가 오고, 결과에 구조 결과가 실린다.
    /// 그 스트림을 그대로 드라이버에 재생해 감시가 정상 실행을 닫지 않고 구조 결과가 나오는지 본다. 시스템 지시(서버 이름·도구 목록)가
    /// 모델 요청 본문에 실제로 실리는지도 본다(잠금은 MCP 도구 안내를 빼므로 모델이 서버 이름을 모른다).
    /// </summary>
    [AgyEndToEndFact]
    public async Task A_schema_run_ends_with_the_finish_step_the_driver_accepts_and_the_tool_list_prompt_reaches_the_model()
    {
        const string Schema = """{"type":"object","additionalProperties":false,"required":["summary"],"properties":{"summary":{"type":"string"}}}""";
        var tools = new[]
        {
            new AgentToolInfo("echo_probe", "Echo the given text back.", JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")!),
        };
        var calls = new[]
        {
            ("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "echo_probe", ["Arguments"] = new JsonObject { ["text"] = "hi" } }),
            ("finish", new JsonObject { ["summary"] = "done by the fake model" }),
        };

        var run = await RunAgyAsync(calls, "fake-run-token-" + Guid.NewGuid().ToString("N"), schema: Schema, tools: tools);

        // 모델 요청: 서버 이름·도구 이름·입력 스키마가 시스템 지시로 실렸다.
        var first = run.RequestBodies.First(b => b.Contains("functionDeclarations", StringComparison.Ordinal));
        using (var request = JsonDocument.Parse(first))
        {
            var instruction = request.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
            Assert.Contains("\"orandactl\"", instruction, StringComparison.Ordinal);
            Assert.Contains("echo_probe", instruction, StringComparison.Ordinal);
            Assert.Contains("\"required\":[\"text\"]", instruction, StringComparison.Ordinal);
            Assert.Contains("call_mcp_tool", instruction, StringComparison.Ordinal);
        }

        // 스트림: 알려진 단계만 오고, finish 단계가 있으며, 결과에 구조 결과가 있다.
        var events = run.Stream.SelectMany(AgyStreamParser.Parse).ToList();
        Assert.DoesNotContain(events, e => e is AgyUnknownStep);
        Assert.Contains(events, e => e is AgyFinishStep);
        var result = events.OfType<AgyResult>().Single();
        Assert.Equal("SUCCESS", result.Status);
        Assert.Contains("done by the fake model", result.StructuredOutputJson, StringComparison.Ordinal);

        // 같은 스트림을 드라이버에 재생한다.
        var tokens = new FakeRunTokens();
        var replayGate = new FakeGate();
        var launcher = new FakeLauncher
        {
            Lines = run.Stream,

            // 실제 실행에서는 orandactl → 게이트가 판단을 내고 그 뒤에 agy 가 DONE 을 보낸다. 재생에서는 게이트 판단을 대신 낸다.
            OnStart = _ => replayGate.Raise(new AgentToolActivityEventArgs("run-1", 1, "echo_probe", AiToolJudgement.Allowed, 3, null)),
        };
        var agy = Path.Combine(_dir, "agy");
        var orandactl = Path.Combine(_dir, "orandactl");
        var driver = new AgyDriver(
            tokens, replayGate, new FixedEndpoint("pipe"), new AppDataPaths(_dir), launcher, orandactl,
            () => _dir, path => path == agy || path == orandactl, _dir, TimeSpan.FromMinutes(1), (_, _, _) => Task.FromResult<string?>("1.4.12"));
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        using var schemaDocument = JsonDocument.Parse(Schema);
        var replay = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(
            new AiRequest("prompt", context, OutputSchema: schemaDocument.RootElement.Clone(), Session: new AgentSession(new AgentRunScope("run-1", profile, context))),
            CancellationToken.None))
        {
            replay.Add(e);
        }

        Assert.DoesNotContain(replay, e => e is AiError);
        Assert.Contains(replay, e => e is AiToolCall { ToolName: "echo_probe" });
        var final = replay.OfType<AiFinal>().Single();
        Assert.Equal("done by the fake model", final.Result!.Value.GetProperty("summary").GetString());
    }

    /// <summary>
    /// MCP 도구 호출 하나의 제한은 서버 항목의 <c>timeoutSeconds</c> 로 정해진다(agy 기본은 정확히 180초). 25초로 두면 40초 걸리는 호출은 25초에
    /// 오류 단계(<c>tool_info.error</c>)로 끊기고 20초 걸리는 호출은 끝난다. 드라이버는 이 값을 6시간으로 쓴다(<see cref="AgyAgentFile.DefaultToolTimeoutSeconds"/>).
    /// 끊긴 호출은 도구 사용 집계에서 실행 뒤 실패로 센다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task The_tool_call_limit_follows_timeoutSeconds_of_the_server_entry()
    {
        static (string, JsonObject)[] Slow(int seconds) =>
            [("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "slow_probe", ["Arguments"] = new JsonObject { ["seconds"] = seconds } })];

        using var cut = new AgyEndToEndTests();
        var cutRun = await cut.RunAgyAsync(Slow(40), null, printTimeout: TimeSpan.FromSeconds(300), toolTimeoutSeconds: 25);
        var failed = cutRun.Stream.SelectMany(AgyStreamParser.Parse).OfType<AgyToolStep>().Single(s => s.State == "ERROR");
        Assert.Contains("timed out after 25s", failed.ErrorMessage, StringComparison.Ordinal);

        using var kept = new AgyEndToEndTests();
        var keptRun = await kept.RunAgyAsync(Slow(20), null, printTimeout: TimeSpan.FromSeconds(300), toolTimeoutSeconds: 25);
        Assert.Contains(keptRun.Stream.SelectMany(AgyStreamParser.Parse).OfType<AgyToolStep>(), s => s.State == "DONE" && s.Output == "slept:20");
    }

    /// <summary>
    /// 측정: MCP 결과가 <c>isError: true</c> 일 때 agy 가 도구 단계를 어떤 상태로 보내는지(브리지의 busy·내부 오류도 이 모양으로 온다).
    /// 측정으로 정해진 동작을 고정한다.
    /// </summary>
    [AgyEndToEndFact]
    public async Task An_MCP_result_with_isError_is_reported_by_agy_as_an_ERROR_step()
    {
        using var cut = new AgyEndToEndTests();
        var run = await cut.RunAgyAsync(
            [("call_mcp_tool", new JsonObject { ["ServerName"] = "orandactl", ["ToolName"] = "error_probe", ["Arguments"] = new JsonObject() })],
            null,
            printTimeout: TimeSpan.FromSeconds(60));
        var steps = run.Stream.SelectMany(AgyStreamParser.Parse).OfType<AgyToolStep>().Where(s => s.ToolName == "call_mcp_tool").ToList();

        // 측정 결과(agy 1.4.12): ACTIVE 다음 ERROR. DONE 으로 보내지 않는다. 그래도 드라이버는 게이트 판단이 없는 DONE 을 성공으로 세지 않는다.
        Assert.Equal(["ACTIVE", "ERROR"], steps.Select(s => s.State));
    }

    private static JsonObject ArgumentsFor(string tool, string marker) => tool switch
    {
        "run_command" => new JsonObject { ["CommandLine"] = $"echo x > {marker}", ["WaitMsBeforeAsync"] = 1000, ["toolSummary"] = "e" },
        "read_file" => new JsonObject { ["Path"] = marker },
        "view_file" => new JsonObject { ["AbsolutePath"] = marker },
        "write_file" => new JsonObject { ["Path"] = marker, ["Content"] = "x" },
        "write_to_file" => new JsonObject { ["TargetFile"] = marker, ["CodeContent"] = "x" },
        "edit_file" or "sed_file" => new JsonObject { ["Path"] = marker },
        "grep_search" => new JsonObject { ["Query"] = "x", ["SearchPath"] = SafeDirectory },
        "list_dir" => new JsonObject { ["DirectoryPath"] = SafeDirectory },
        "find_by_name" => new JsonObject { ["SearchDirectory"] = SafeDirectory, ["Pattern"] = "*" },
        "search_web" => new JsonObject { ["query"] = "example" },
        "read_url_content" or "open_browser_url" => new JsonObject { ["Url"] = "https://example.invalid" },
        "execute_browser_javascript" => new JsonObject { ["Script"] = "1" },
        "browser_subagent" => new JsonObject { ["Task"] = "x" },
        "invoke_subagent" => new JsonObject { ["Subagents"] = new JsonArray() },
        "define_subagent" => new JsonObject { ["Name"] = "x" },
        "manage_subagents" or "schedule" => new JsonObject { ["Action"] = "list" },
        "send_message" => new JsonObject { ["To"] = "x", ["Message"] = "x" },
        "generate_image" => new JsonObject { ["Prompt"] = "x" },
        "run_workflow" => new JsonObject { ["Name"] = "x" },
        _ => new JsonObject { ["Question"] = "x" },
    };

    private static string SafeDirectory => Path.GetTempPath();

    /// <summary>미끼를 놓는 쪽에 주는 정보: 미끼 HOME, 미끼 스크립트, 기록 파일, 이 실행의 실행 폴더.</summary>
    private sealed record DecoyContext(string Home, string Script, string Log, string RunDirectory)
    {
        public string[] Command(string label, string mode) => ["node", Script, Log, label, mode];

        public static async Task WriteAsync(string absolutePath, JsonNode content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, content.ToJsonString());
        }

        /// <summary>MCP 설정(<c>mcpServers</c>) 한 개.</summary>
        public JsonObject McpConfig(string label) => new JsonObject
        {
            ["mcpServers"] = new JsonObject { [label] = new JsonObject { ["command"] = "node", ["args"] = new JsonArray(Script, Log, label, "mcp") } },
        };

        /// <summary>훅 설정 한 개. 시작·호출 전후·종료 사건은 처리자 목록을 바로, 도구 사건은 matcher 묶음으로 쓴다(내장 문서의 형식).</summary>
        public JsonObject HooksConfig(string label)
        {
            JsonObject Handler() => new() { ["type"] = "command", ["command"] = string.Join(' ', Command(label, "once")) };
            JsonArray Flat() => new(Handler());
            JsonArray Grouped() => new(new JsonObject { ["matcher"] = "*", ["hooks"] = new JsonArray(Handler()) });
            return new JsonObject
            {
                [label] = new JsonObject
                {
                    ["SessionStart"] = Flat(),
                    ["PreInvocation"] = Flat(),
                    ["PostInvocation"] = Flat(),
                    ["Stop"] = Flat(),
                    ["PreToolUse"] = Grouped(),
                    ["PostToolUse"] = Grouped(),
                },
            };
        }

        /// <summary>사이드카 정의 한 개.</summary>
        public JsonObject Sidecar(string label) => new JsonObject
        {
            ["command"] = "node",
            ["args"] = new JsonArray(Script, Log, label, "once"),
            ["restart_policy"] = "never",
            ["description"] = "decoy",
        };
    }

    private async Task<AgyRun> RunAgyAsync(
        IReadOnlyList<(string Name, JsonObject Args)> calls,
        string? token = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        Func<DecoyContext, Task>? arrange = null,
        string? runRelativeToHome = null,
        bool locked = true,
        bool allowCommands = false,
        string? shell = null,
        TimeSpan? killAfter = null,
        string? schema = null,
        IReadOnlyList<AgentToolInfo>? tools = null,
        TimeSpan? printTimeout = null,
        int toolTimeoutSeconds = AgyAgentFile.DefaultToolTimeoutSeconds)
    {
        var located = SystemCliExecutable.Locate("agy", "agy", Environment.GetEnvironmentVariable("PATH"), File.Exists);
        Assert.True(located.Executable is not null, "agy 실행 파일을 찾지 못했습니다: " + located.Error?.Message);

        // 미끼 HOME: 사용자 전역 설정(과 실제 대화 기록)이 아니라 이 빈 폴더를 agy 가 쓰게 한다. 실행 폴더는 기본으로 HOME 밖이고,
        // runRelativeToHome 을 주면 앱처럼 HOME 아래(앱 데이터 폴더)에 둔다.
        var decoyHome = Path.Combine(_dir, "home");
        Directory.CreateDirectory(decoyHome);
        var runDirectory = runRelativeToHome is null ? Path.Combine(_dir, "run") : Path.Combine(decoyHome, runRelativeToHome);
        Directory.CreateDirectory(runDirectory);
        var fakeMcp = Path.Combine(_dir, "agy-fake-mcp.mjs");
        File.Copy(FixturePath("agy-fake-mcp.mjs"), fakeMcp);
        var wrapper = Path.Combine(_dir, "fake-orandactl");
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nexec node '{fakeMcp}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var agentFile = AgyAgentFile.Write(runDirectory, wrapper, "unused-pipe", withFinishTool: schema is not null, tools, toolTimeoutSeconds);

        using var server = new FakeModelServer(calls, block: killAfter is not null);
        var arguments = AgyCliArguments.Build(new AgyInvocation(null, schema)).ToList();

        // 모델 제공자만 가짜 서버(GEMINI_API_KEY 경로)로 바꾸고, 잠금 규칙은 드라이버가 쓰는 값 그대로 둔다.
        var settings = JsonNode.Parse(AgyCliArguments.SettingsJson)!.AsObject();
        settings["modelProvider"] = "gemini";
        arguments[arguments.IndexOf("--settings") + 1] = settings.ToJsonString();
        arguments.AddRange(["--print-timeout", printTimeout is { } pt ? $"{(int)pt.TotalSeconds}s" : "60s"]);
        if (allowCommands)
        {
            // 대조군 전용: 명령 거부 규칙을 빼서 로그인 셸이 실제로 뜨는지 본다.
            arguments[arguments.IndexOf("--settings") + 1] = new JsonObject
            {
                ["modelProvider"] = "gemini",
                ["permissions"] = new JsonObject { ["allow"] = new JsonArray("command(*)") },
            }.ToJsonString();
        }

        if (!locked)
        {
            // 대조군: 잠금 에이전트 없이(기본 에이전트) 같은 미끼가 뜨는지 본다. 뜨지 않으면 미끼 형식이 틀려 측정이 비어 있다는 뜻이다.
            arguments.RemoveRange(arguments.IndexOf("--agent"), 2);
        }

        // 전역·상위 폴더의 미끼는 호출한 시험이 놓는다. 미끼가 뜨면 기록 파일에 이름표가 한 줄 남는다.
        var decoyLog = Path.Combine(_dir, "decoy.log");
        if (arrange is not null)
        {
            var decoyScript = Path.Combine(_dir, "agy-decoy-mcp.mjs");
            File.Copy(FixturePath("agy-decoy-mcp.mjs"), decoyScript);
            await arrange(new DecoyContext(decoyHome, decoyScript, decoyLog, runDirectory));
        }

        // 드라이버가 쓰는 실행 정보 조합을 그대로 쓴다: 토큰은 환경 변수로만 넘긴다.
        var launch = new ClaudeLaunch(
            located.Executable!,
            arguments,
            runDirectory,
            AgyCliArguments.BuildStandardInput("probe"),
            AgyDriver.LaunchEnvironment(token));
        var info = SystemClaudeProcessLauncher.CreateStartInfo(launch);
        info.Environment["HOME"] = decoyHome;
        var tempDirectory = Path.Combine(_dir, "tmp");
        Directory.CreateDirectory(tempDirectory);
        info.Environment["TMPDIR"] = tempDirectory;
        info.Environment["GEMINI_API_KEY"] = "probe-fake-key";
        info.Environment["GOOGLE_GEMINI_BASE_URL"] = server.BaseUrl;
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        if (shell is not null)
        {
            info.Environment["SHELL"] = shell;
        }

        using var process = Process.Start(info)!;
        var tree = new HashSet<string>(StringComparer.Ordinal);
        var treePids = new Dictionary<int, string>();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                foreach (var (pid, command) in await DescendantCommandsAsync(process.Id))
                {
                    lock (tree)
                    {
                        tree.Add(command);
                        treePids[pid] = command;
                    }
                }

                await Task.Delay(50);
            }
        });
        await process.StandardInput.WriteAsync(launch.StandardInput);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(printTimeout is { } pt2 ? pt2 + TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(120));
        if (killAfter is { } delay)
        {
            // 드라이버가 죽이는 경우를 흉내 낸다: SystemClaudeProcess.Kill 과 같은 호출(프로세스 트리 전체 종료).
            await Task.Delay(delay);
            process.Kill(entireProcessTree: true);
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await sampling.CancelAsync();
        await sampler;
        // 부모가 끝난 뒤에도 자손이 남는지 본다: 1초를 기다린 뒤 살아 있는 자손을 기록하고(환경에 토큰 변수가 있는지는 이름만 본다),
        // 최대 20초까지 지켜보며 언제까지 사는지 잰다. 끝까지 남은 것은 시험이 띄운 것이므로 시험이 정리한다.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var survivors = new List<string>();
        var pending = treePids.Where(p => IsAlive(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        foreach (var (pid, command) in pending)
        {
            survivors.Add($"{command} token={(await EnvironmentHasTokenAsync(pid) ? "yes" : "no")}");
        }

        var watchStart = DateTime.UtcNow;
        while (pending.Count > 0 && DateTime.UtcNow - watchStart < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(300);
            foreach (var pid in pending.Keys.ToList().Where(pid => !IsAlive(pid)))
            {
                pending.Remove(pid);
            }
        }

        if (survivors.Count > 0)
        {
            survivors.Add($"survivedAfterParentExit={(long)(DateTime.UtcNow - watchStart).TotalMilliseconds + 1000}ms stillAliveAfter20s={pending.Count}");
        }

        foreach (var pid in pending.Keys)
        {
            try
            {
                using var alive = Process.GetProcessById(pid);
                alive.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 이미 끝났다.
            }
        }

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _ = await stderr;
        return new AgyRun(lines, server.DeclaredTools, server.ToolResponses, server.RequestBodies, arguments, decoyHome, agentFile, decoyLog, tempDirectory, [.. tree.Order()], survivors);
    }

    /// <summary>agy(주어진 pid)의 자손 프로세스 명령을 한 번 표본으로 뜬다. 명령 글은 첫 단어의 파일 이름과 짧은 인자만 남긴다(경로·토큰은 남기지 않는다).</summary>
    private static async Task<List<(int Pid, string Command)>> DescendantCommandsAsync(int rootPid)
    {
        using var ps = Process.Start(new ProcessStartInfo("ps", ["-axo", "pid=,ppid=,command="]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = await ps.StandardOutput.ReadToEndAsync();
        await ps.WaitForExitAsync();
        var rows = new List<(int Pid, int Parent, string Command)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var parent))
            {
                rows.Add((pid, parent, parts[2]));
            }
        }

        var found = new HashSet<int> { rootPid };
        var result = new List<(int Pid, string Command)>();
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, parent, command) in rows)
            {
                if (found.Contains(parent) && found.Add(pid))
                {
                    grew = true;
                    var words = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    var head = Path.GetFileName(words[0]);
                    result.Add((pid, words.Length > 1 && words[1].StartsWith('-') ? head + " " + words[1] : head));
                }
            }
        }

        return result;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var alive = Process.GetProcessById(pid);
            return !alive.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>프로세스 환경에 실행 토큰 변수가 있는지(값은 읽지 않고 이름만 본다). macOS 의 <c>ps eww</c> 는 같은 사용자 프로세스의 환경을 명령 뒤에 붙여 준다.</summary>
    private static async Task<bool> EnvironmentHasTokenAsync(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("ps", ["eww", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "command="]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = await ps.StandardOutput.ReadToEndAsync();
        await ps.WaitForExitAsync();
        return output.Contains("ORANDA_RUN_TOKEN=", StringComparison.Ordinal);
    }

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name);
    }

    internal sealed record AgyRun(
        string[] Stream,
        IReadOnlyList<string> DeclaredTools,
        IReadOnlyList<string> ToolResponses,
        IReadOnlyList<string> RequestBodies,
        IReadOnlyList<string> Arguments,
        string DecoyHome,
        string AgentFile,
        string DecoyLog,
        string TempDirectory,
        IReadOnlyList<string> ProcessTree,
        IReadOnlyList<string> SurvivingChildren);

    /// <summary>
    /// 대본형 가짜 Gemini 서버. 도구 선언이 든 요청에는 대본의 functionCall 을 하나씩 돌려주고 끝나면 글로 끝낸다.
    /// 도구 선언이 없는 요청(대화 제목 생성)에는 짧은 글을 돌려준다. 받은 요청의 도구 선언과 functionResponse 를 모은다.
    /// </summary>
    internal sealed class FakeModelServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly IReadOnlyList<(string Name, JsonObject Args)> _calls;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly List<string> _responses = [];
        private readonly List<string> _bodies = [];
        private readonly object _gate = new();
        private int _step;

        private readonly bool _block;

        public FakeModelServer(IReadOnlyList<(string Name, JsonObject Args)> calls, bool block = false)
        {
            _calls = calls;
            _block = block;
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> DeclaredTools
        {
            get
            {
                lock (_gate)
                {
                    return [.. _declared];
                }
            }
        }

        public IReadOnlyList<string> ToolResponses
        {
            get
            {
                lock (_gate)
                {
                    return [.. _responses];
                }
            }
        }

        public IReadOnlyList<string> RequestBodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _bodies];
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // 닫는 중의 종료 예외는 무시한다.
            }

            _stop.Dispose();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            if (_block)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default); // 응답하지 않는다(시간 초과·강제 종료 시나리오)
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            JsonNode? request = null;
            try
            {
                request = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                // 본문이 JSON 이 아니면 선언·응답 수집만 건너뛴다.
            }

            var declarations = request?["tools"]?.AsArray()
                .SelectMany(t => t?["functionDeclarations"]?.AsArray() ?? [])
                .Select(f => f?["name"]?.GetValue<string>() ?? string.Empty)
                .ToList() ?? [];
            JsonNode answer;
            lock (_gate)
            {
                _bodies.Add(body);
                foreach (var name in declarations)
                {
                    _declared.Add(name);
                }

                if (request?["contents"]?.AsArray() is { } contents && declarations.Count > 0)
                {
                    var last = contents.LastOrDefault()?["parts"]?.AsArray();
                    foreach (var part in last ?? [])
                    {
                        if (part?["functionResponse"] is { } response)
                        {
                            _responses.Add(response.ToJsonString());
                        }
                    }
                }

                if (declarations.Count == 0)
                {
                    answer = TextAnswer("Probe Title");
                }
                else if (_step < _calls.Count)
                {
                    var call = _calls[_step++];
                    answer = new JsonObject
                    {
                        ["candidates"] = new JsonArray(new JsonObject
                        {
                            ["content"] = new JsonObject
                            {
                                ["role"] = "model",
                                ["parts"] = new JsonArray(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = call.Args.DeepClone() } }),
                            },
                            ["finishReason"] = "STOP",
                        }),
                        ["usageMetadata"] = Usage(),
                    };
                }
                else
                {
                    answer = TextAnswer("probe-done");
                }
            }

            var json = answer.ToJsonString();
            var sse = context.Request.Url!.Query.Contains("alt=sse", StringComparison.Ordinal) || context.Request.Url.AbsolutePath.Contains("stream", StringComparison.OrdinalIgnoreCase);
            var payload = Encoding.UTF8.GetBytes(sse ? "data: " + json + "\n\n" : json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = sse ? "text/event-stream" : "application/json";
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }

        private static JsonObject Usage() => new() { ["promptTokenCount"] = 1, ["candidatesTokenCount"] = 1, ["totalTokenCount"] = 2 };

        private static JsonObject TextAnswer(string text) => new()
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = "STOP",
            }),
            ["usageMetadata"] = Usage(),
        };
    }
}

/// <summary>환경 변수 <c>ORANDA_AI_E2E_AGY=1</c> 일 때만 실제 agy 실행 파일을 부른다(가짜 모델 서버만 쓴다).</summary>
public sealed class AgyEndToEndFactAttribute : FactAttribute
{
    public AgyEndToEndFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E_AGY") != "1")
        {
            Skip = "ORANDA_AI_E2E_AGY=1 일 때만 실제 agy 를 부른다.";
        }
        else if (OperatingSystem.IsWindows())
        {
            Skip = "macOS 전용 시험이다(Windows 는 미검증).";
        }
    }
}
