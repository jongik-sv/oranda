using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Platform.Ai.Bridge;
using Oranda.Platform.Ai.Cli;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 지금 설치된 codex 가 우리 인자로 모델 서버에 보내는 요청의 도구를 확인한다. 실제 로그인·실제 모델 호출은 없다: 가짜 모델 서버(HttpListener)와
/// 빈 임시 <c>CODEX_HOME</c> 을 쓰고 키는 가짜 값이다(~/.codex 를 읽지 않는다). 환경 변수 <c>ORANDA_AI_E2E_CODEX=1</c> 일 때만 돈다.
/// CLI 를 갱신한 뒤 잠금이 깨지는 것을 잡는 유일한 수단이다.
/// 도구는 요청 본문 최상위 <c>tools</c> 에만 있지 않고 <c>input</c> 안의 <c>additional_tools</c> 항목으로도 들어오므로 둘 다 본다.
/// 대조 실행(카탈로그 덮어쓰기 없음)이 금지 도구를 <b>보여야</b> 검사기가 올바른 자리를 보고 있다는 증거가 된다.
/// </summary>
[Collection(RealClaudeGroup.Name)]
public sealed class CodexEndToEndTests : IDisposable
{
    private static readonly string[] AllowedBare = ["list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource", "request_user_input"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-codex-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _codexHome;
    private readonly Dictionary<string, string?> _savedEnv = [];

    public CodexEndToEndTests()
    {
        Directory.CreateDirectory(_dir);
        _codexHome = Path.Combine(_dir, "codex-home");
        Directory.CreateDirectory(_codexHome);
        foreach (var (name, value) in new[] { ("CODEX_HOME", _codexHome), ("OPENAI_API_KEY", "fake-key-not-real") })
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [CodexEndToEndFact]
    public async Task Installed_codex_is_a_version_whose_lock_was_verified()
    {
        var output = await SystemCliVersion.ReadAsync(FindCodex(), TimeSpan.FromSeconds(20), CancellationToken.None);

        var version = SystemCliVersion.Parse(output);
        Assert.NotNull(version);
        Assert.Contains(version, CodexDriver.VerifiedVersions); // 설치된 codex 가 바뀌면 잠금 시험(이 클래스 전체)을 다시 돌려 검증 목록을 갱신한다
    }

    [CodexEndToEndFact]
    public async Task The_control_run_shows_forbidden_tools_so_the_detector_looks_in_the_right_place()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        await using var server = new FakeModelServer([]);

        await RunRawAsync(ControlArguments(server.Port, model), server, "hi");

        var declared = Assert.Single(server.Requests.Take(1).Select(DeclaredTools));
        Assert.NotEmpty(Violations(declared)); // 대조: 잠그지 않으면 exec·apply_patch 등이 선언된다
    }

    [CodexEndToEndFact]
    public async Task Locked_arguments_declare_only_allowed_tools_and_forced_builtin_calls_are_unsupported()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var forced = new[]
        {
            Function("shell", """{"command":["bash","-lc","echo x"]}"""),
            Function("shell_command", """{"command":"echo x"}"""),
            Function("exec_command", """{"cmd":"echo x"}"""),
            Function("write_stdin", """{"session_id":1,"chars":"x"}"""),
            Function("apply_patch", """{"input":"x"}"""),
            Custom("apply_patch", "*** Begin Patch\n*** End Patch\n"),
            Custom("exec", "text(1)"),
            Function("view_image", """{"path":"/etc/hosts"}"""),
            Function("web_search", """{"query":"x"}"""),
            Function("spawn_agent", """{"task_name":"t","message":"x"}"""),
            Function("tool_search", """{"query":"x"}"""),
        };
        await using var server = new FakeModelServer(forced);
        var launcher = new RecordingLauncher(server.Port, model, FindFixture("codex-mcp-server.mjs"), Path.Combine(_dir, "mcp.log"));
        var run = Path.Combine(_dir, "run");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "unused"), "x");
        var catalog = await LockedCatalogAsync(run);
        var invocation = new CodexInvocation(null, run, catalog, launcher.OrandactlWrapper(_dir), "pipe-x", "ORANDA_RUN_TOKEN");
        var arguments = launcher.WithFakeProvider(CodexArguments.Build(invocation));

        await RunRawAsync(arguments, server, "hi", run);

        Assert.NotEmpty(server.Requests); // 가짜 서버가 요청을 받았다
        foreach (var body in server.Requests)
        {
            var declared = DeclaredTools(body);
            Assert.Empty(Violations(declared));
            AssertNoCodexApps(body);
        }

        // 요청에 쓴 모델은 원본 카탈로그에서 내장 도구를 가진 모델이어야 이 시험이 뜻을 가진다.
        Assert.Equal(model, JsonNode.Parse(server.Requests[0])!["model"]!.GetValue<string>());
        var outputs = server.Requests.Skip(1).SelectMany(FunctionOutputs).ToList();
        Assert.True(outputs.Count >= forced.Length, $"강제 호출 결과가 {outputs.Count}개뿐이다.");
        Assert.All(outputs, output => Assert.Contains("unsupported", output, StringComparison.OrdinalIgnoreCase));
    }

    [CodexEndToEndFact]
    public async Task The_driver_runs_a_tool_call_then_resumes_from_another_folder_with_the_same_lock()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var mcpLog = Path.Combine(_dir, "mcp.log");
        var launcherHelper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), mcpLog);
        var wrapper = launcherHelper.OrandactlWrapper(_dir);
        await using var server = new FakeModelServer([McpCall("echo_probe", """{"text":"hello"}""")]);
        var gate = new FakeGate();
        var launcher = new RecordingLauncher(server.Port, model, FindFixture("codex-mcp-server.mjs"), mcpLog) { OnStart = () => RaiseAllowed(gate, "echo_probe") };
        var tokens = new FakeRunTokens();
        var driver = new CodexDriver(
            tokens,
            gate,
            new FixedEndpoint("pipe-x"),
            new AppDataPaths(_dir),
            launcher.AsLauncher(),
            wrapper,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable,
            _dir,
            TimeSpan.FromMinutes(2));

        var first = await CollectAsync(driver, Request("먼저 echo_probe 도구를 부르세요."));

        Assert.DoesNotContain(first, e => e is AiError);
        Assert.Contains(first, e => e is AiToolCall { ToolName: "echo_probe" });
        var final = Assert.IsType<AiFinal>(first[^1]);
        Assert.False(string.IsNullOrEmpty(final.ResumeId));
        Assert.Equal(tokens.Issued, tokens.Revoked);

        // 이어 말하기: 실행 폴더는 새로 만들어지고(이전 폴더는 이미 지워졌다) 잠금 인자는 다시 전부 주어진다.
        server.Reset([]);
        // 이미지를 붙여도 -i 가 대화 id 를 삼키지 않고 이어 말하기가 된다.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var second = await CollectAsync(driver, Request("이어서 답하세요.", final.ResumeId) with { Images = [new AiImageAttachment("image/png", png)] });

        Assert.DoesNotContain(second, e => e is AiError);
        Assert.IsType<AiFinal>(second[^1]);
        Assert.NotEmpty(server.Requests);
        Assert.Contains(server.Requests, r => r.Contains("input_image", StringComparison.Ordinal));
        foreach (var body in server.Requests)
        {
            Assert.Empty(Violations(DeclaredTools(body)));
            AssertNoCodexApps(body);
        }

        // 토큰은 MCP 서버(orandactl 자리)가 환경 변수로 물려받았다. 값은 로그에 적지 않고 설정 여부만 본다.
        Assert.Contains("\"probeEnv\":\"set\"", File.ReadAllText(mcpLog), StringComparison.Ordinal);
    }

    [CodexEndToEndFact]
    public async Task No_file_left_after_a_run_contains_the_run_token_and_the_session_files_really_exist()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var mcpLog = Path.Combine(_dir, "mcp.log");
        var helper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), mcpLog);
        var wrapper = helper.OrandactlWrapper(_dir);
        await using var server = new FakeModelServer([McpCall("echo_probe", """{"text":"hello"}""")]);
        var tokens = new FakeRunTokens();
        var gate = new FakeGate();
        var driver = NewDriver(tokens, new RecordingLauncher(server.Port, model, FindFixture("codex-mcp-server.mjs"), mcpLog) { OnStart = () => RaiseAllowed(gate, "echo_probe") }.AsLauncher(), wrapper, gate: gate);

        var events = await CollectAsync(driver, Request("echo_probe 를 부르세요."));

        Assert.DoesNotContain(events, e => e is AiError);
        var token = Assert.Single(tokens.Issued);
        // 토큰을 실제로 물려받은 실행에서만 검사한다(받지 않은 실행의 부재 확인은 빈 통과다).
        Assert.Contains("\"probeEnv\":\"set\"", File.ReadAllText(mcpLog), StringComparison.Ordinal);

        var sessionFiles = Directory.Exists(Path.Combine(_codexHome, "sessions"))
            ? Directory.GetFiles(Path.Combine(_codexHome, "sessions"), "*", SearchOption.AllDirectories)
            : [];
        Assert.NotEmpty(sessionFiles); // 대화 기록 파일이 실제로 생겼다(빈 통과 방지)
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            scanned++;
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }

        Assert.True(scanned >= sessionFiles.Length);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "ai", "runs"))); // 실행 폴더는 지워졌다
    }

    [CodexEndToEndFact]
    public async Task A_decoy_global_mcp_server_does_not_start_under_the_lock_but_does_without_it()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var decoyMark = Path.Combine(_dir, "decoy-started.txt");
        File.WriteAllText(
            Path.Combine(_codexHome, "config.toml"),
            $"[mcp_servers.decoy]\ncommand = \"/bin/sh\"\nargs = [\"-c\", \"env > '{decoyMark}'\"]\n");

        // 대조: 잠금(--ignore-user-config) 없이는 미끼 MCP 프로세스가 떠서 표지 파일이 생긴다. 이것이 측정이 유효하다는 증거다.
        await using (var control = new FakeModelServer([]))
        {
            var withoutIgnore = new RecordingLauncher(control.Port, model, string.Empty, string.Empty).WithFakeProvider(
                ["exec", "--json", "--skip-git-repo-check", "--ignore-rules", "-s", "read-only", "-"]);
            await RunRawAsync(withoutIgnore, control, "hi");
        }

        Assert.True(File.Exists(decoyMark), "대조 실행에서 미끼 MCP 가 뜨지 않아 측정이 유효하지 않다.");
        File.Delete(decoyMark);

        // 앱처럼 HOME 아래(여기서는 임시 HOME)에 실행 폴더를 두고 드라이버로 돈다.
        var mcpLog = Path.Combine(_dir, "mcp.log");
        var helper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), mcpLog);
        var wrapper = helper.OrandactlWrapper(_dir);
        await using var server = new FakeModelServer([]);
        var tokens = new FakeRunTokens();
        var driver = NewDriver(tokens, new RecordingLauncher(server.Port, model, FindFixture("codex-mcp-server.mjs"), mcpLog).AsLauncher(), wrapper);

        var events = await CollectAsync(driver, Request("답하세요."));

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.False(File.Exists(decoyMark), "잠금 상태에서 전역 MCP 미끼가 시작됐다(토큰 상속 위험).");
    }

    [CodexEndToEndFact]
    public async Task The_shell_snapshot_that_spawns_the_login_shell_and_writes_the_environment_is_off_under_the_lock_and_on_in_the_control()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var fakeHome = Path.Combine(_dir, "fake-zdotdir");
        Directory.CreateDirectory(fakeHome);
        var marker = Path.Combine(_dir, "zsh-started.txt");
        File.WriteAllText(
            Path.Combine(fakeHome, ".zshenv"),
            $"echo started >> '{marker}'\n[ -n \"$ORANDA_RUN_TOKEN\" ] && echo token-present >> '{marker}'\n");
        _savedEnv.TryAdd("ZDOTDIR", Environment.GetEnvironmentVariable("ZDOTDIR"));
        Environment.SetEnvironmentVariable("ZDOTDIR", fakeHome);
        var snapshots = Path.Combine(_codexHome, "shell_snapshots");
        var helper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), Path.Combine(_dir, "mcp.log"));

        // 대조: 같은 잠금에서 shell_snapshot 줄만 뺀다. 미끼 rc 파일이 토큰이 든 환경에서 실행되고 스냅숏 파일이 생긴다(측정이 유효하다는 증거).
        await using (var control = new FakeModelServer([]) { Delay = TimeSpan.FromMinutes(1) })
        {
            var run = Path.Combine(_dir, "run-control");
            Directory.CreateDirectory(run);
            var arguments = CodexArguments.Build(new CodexInvocation(null, run, await LockedCatalogAsync(run), helper.OrandactlWrapper(_dir), "pipe-x", "ORANDA_RUN_TOKEN")).ToList();
            var at = arguments.IndexOf("features.shell_snapshot=false");
            arguments.RemoveRange(at - 1, 2);
            await RunRawAsync(new RecordingLauncher(control.Port, model, string.Empty, string.Empty).WithFakeProvider(arguments), control, "hi", run, TimeSpan.FromSeconds(8));
        }

        Assert.True(File.Exists(marker), "대조 실행에서 로그인 셸이 뜨지 않아 측정이 유효하지 않다.");
        Assert.Contains("token-present", File.ReadAllText(marker), StringComparison.Ordinal);
        Assert.NotEmpty(Directory.Exists(snapshots) ? Directory.GetFiles(snapshots) : []);
        Assert.Contains(Directory.GetFiles(snapshots), f => File.ReadAllText(f).Contains("fake-token-not-real", StringComparison.Ordinal));

        // 잠금: 셸 기동도 스냅숏 파일도 없다.
        File.Delete(marker);
        foreach (var file in Directory.GetFiles(snapshots))
        {
            File.Delete(file);
        }

        await using var locked = new FakeModelServer([]) { Delay = TimeSpan.FromMinutes(1) };
        var lockedRun = Path.Combine(_dir, "run-locked");
        Directory.CreateDirectory(lockedRun);
        var lockedArguments = CodexArguments.Build(new CodexInvocation(null, lockedRun, await LockedCatalogAsync(lockedRun), helper.OrandactlWrapper(_dir), "pipe-x", "ORANDA_RUN_TOKEN"));
        await RunRawAsync(new RecordingLauncher(locked.Port, model, string.Empty, string.Empty).WithFakeProvider(lockedArguments), locked, "hi", lockedRun, TimeSpan.FromSeconds(8));

        Assert.False(File.Exists(marker), "잠금 상태에서 사용자의 로그인 셸이 시작됐다(토큰 노출).");
        Assert.Empty(Directory.Exists(snapshots) ? Directory.GetFiles(snapshots) : []);
    }

    [CodexEndToEndFact]
    public async Task No_token_remains_in_codex_home_or_tmpdir_after_a_forced_kill_of_a_run_that_received_it()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var tmp = Path.Combine(_dir, "tmpdir");
        Directory.CreateDirectory(tmp);
        _savedEnv.TryAdd("TMPDIR", Environment.GetEnvironmentVariable("TMPDIR"));
        Environment.SetEnvironmentVariable("TMPDIR", tmp);
        var mcpLog = Path.Combine(_dir, "mcp.log");
        var wrapper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), mcpLog).OrandactlWrapper(_dir);
        await using var server = new FakeModelServer([McpCall("echo_probe", """{"text":"hello"}""")]) { FinalDelay = TimeSpan.FromMinutes(1) };
        var tokens = new FakeRunTokens();
        var driver = NewDriver(tokens, new RecordingLauncher(server.Port, model, FindFixture("codex-mcp-server.mjs"), mcpLog).AsLauncher(), wrapper);

        // 토큰을 MCP 가 받은 것이 확인되면 취소한다: 드라이버의 정리는 프로세스 트리를 강제 종료한다(정상 종료 때 지워지는 파일이 남는 경로).
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = Task.Run(async () =>
        {
            await foreach (var e in driver.RunAsync(Request("echo_probe 를 부르세요."), cancel.Token))
            {
                _ = e;
            }
        });
        var received = false;
        for (var i = 0; i < 200 && !received; i++)
        {
            await Task.Delay(100);
            received = File.Exists(mcpLog) && File.ReadAllText(mcpLog).Contains("\"probeEnv\":\"set\"", StringComparison.Ordinal);
        }

        Assert.True(received, "MCP 가 토큰을 받은 실행이 아니라 부재 확인이 빈 통과가 된다.");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var token = Assert.Single(tokens.Issued);
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            scanned++;
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
        }

        Assert.True(scanned > 0 && Directory.GetFiles(_codexHome, "*", SearchOption.AllDirectories).Length > 0);
        Assert.Empty(Directory.Exists(Path.Combine(_codexHome, "shell_snapshots")) ? Directory.GetFiles(Path.Combine(_codexHome, "shell_snapshots")) : []);
    }

    /// <summary>
    /// 실제 앱처럼 HOME 아래(HOME/Library/Application Support/Oranda/ai/runs/…)에 실행 폴더를 두고 CODEX_HOME 도 HOME/.codex 로 둔 배치.
    /// 실행 폴더의 상위 폴더들과 HOME 에 미끼(.codex/config.toml 의 MCP·인라인 훅, .codex/hooks.json, .agents/skills, AGENTS.md)를 두고,
    /// 상위에 .git 이 있고 신뢰(trust_level)를 표시한 경우까지 잠금 실행에서 아무것도 뜨지 않는지 본다.
    /// 대조(잠금 없음, 훅 신뢰 우회는 대조에만 쓴다)에서 전역 MCP·훅과 프로젝트 지시문·스킬이 뜨는 것으로 측정이 유효함을 보인다.
    /// 측정 사실: 프로젝트(.git 루트 아래 폴더)의 .codex/config.toml 의 MCP·훅은 신뢰 표시를 해도 대조군에서도 뜨지 않았다(대조군 없음).
    /// </summary>
    [CodexEndToEndFact]
    public async Task Baits_in_parent_folders_and_home_do_not_start_or_enter_the_prompt_under_the_lock_with_the_run_folder_under_home()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var home = Path.Combine(_dir, "app-home");
        var codexHome = Path.Combine(home, ".codex");
        var appData = Path.Combine(home, "Library", "Application Support", "Oranda");
        var marks = Path.Combine(_dir, "marks");
        Directory.CreateDirectory(marks);
        Directory.CreateDirectory(Path.Combine(appData, "ai", "runs"));
        foreach (var name in new[] { "HOME", "CODEX_HOME" })
        {
            _savedEnv.TryAdd(name, Environment.GetEnvironmentVariable(name));
        }

        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);

        var places = new Dictionary<string, string>
        {
            ["home"] = home,
            ["lib"] = Path.Combine(home, "Library"),
            ["app"] = appData,
            ["ai"] = Path.Combine(appData, "ai"),
            ["runs"] = Path.Combine(appData, "ai", "runs"),
        };
        foreach (var (name, directory) in places)
        {
            var codexDirectory = name == "home" ? codexHome : Path.Combine(directory, ".codex");
            Directory.CreateDirectory(codexDirectory);
            var config = $"[mcp_servers.bait_{name}]\ncommand = \"/bin/sh\"\nargs = [\"-c\", \"echo started > '{marks}/mcp-{name}'\"]\n"
                + $"\n[[hooks.SessionStart]]\n[[hooks.SessionStart.hooks]]\ntype = \"command\"\ncommand = \"/bin/sh -c 'echo x >> {marks}/hook-inline-{name}'\"\ntimeout = 10\n";
            File.AppendAllText(Path.Combine(codexDirectory, "config.toml"), config);
            File.WriteAllText(
                Path.Combine(codexDirectory, "hooks.json"),
                "{\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"/bin/sh -c 'echo x >> " + marks + "/hook-file-" + name + "'\",\"timeout\":10}]}]}}");
            var skill = Path.Combine(directory, ".agents", "skills", "bait" + name);
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: bait{name}\ndescription: SKILLBAIT_{name} marker\n---\nSKILLBAIT_{name}\n");
            File.WriteAllText(Path.Combine(directory, "AGENTS.md"), $"AGENTSBAIT_{name}\n");
        }

        // 앱 데이터 폴더를 git 루트로 만들고(그 위로는 지시문을 읽지 않는다) 관련 경로를 모두 신뢰로 적는다(사용자가 신뢰 표시를 해 둔 경우).
        Directory.CreateDirectory(Path.Combine(appData, ".git"));
        var real = (string p) => Path.GetFullPath(new DirectoryInfo(p).ResolveLinkTarget(true)?.FullName ?? p);
        File.AppendAllText(
            Path.Combine(codexHome, "config.toml"),
            string.Concat(new[] { appData, Path.Combine(appData, "ai"), Path.Combine(appData, "ai", "runs"), home }.Select(p => $"\n[projects.\"{real(p)}\"]\ntrust_level = \"trusted\"\n")));

        // 대조: 잠금 없이(훅 신뢰 우회 포함) 같은 배치에서 돌려, 전역 MCP·훅과 프로젝트 지시문·스킬이 실제로 들어오는 것을 확인한다.
        var controlRun = Path.Combine(appData, "ai", "runs", "control");
        Directory.CreateDirectory(controlRun);
        await using (var control = new FakeModelServer([]))
        {
            var args = new RecordingLauncher(control.Port, model, string.Empty, string.Empty).WithFakeProvider(
                ["exec", "--json", "--skip-git-repo-check", "--ignore-rules", "-C", controlRun, "-s", "read-only", "-"]);
            args.Insert(args.IndexOf("--ignore-rules"), "--dangerously-bypass-hook-trust");
            await RunRawAsync(args, control, "hi", controlRun);
            var body = string.Concat(control.Requests);
            Assert.True(File.Exists(Path.Combine(marks, "mcp-home")), "대조에서 전역 MCP 미끼가 뜨지 않아 측정이 유효하지 않다.");
            Assert.True(File.Exists(Path.Combine(marks, "hook-inline-home")) || File.Exists(Path.Combine(marks, "hook-file-home")), "대조에서 전역 훅 미끼가 돌지 않았다.");
            Assert.Contains("SKILLBAIT_app", body, StringComparison.Ordinal);
            Assert.Contains("AGENTSBAIT_app", body, StringComparison.Ordinal);
        }

        foreach (var file in Directory.GetFiles(marks))
        {
            File.Delete(file);
        }

        // 잠금: 드라이버가 실행 폴더를 앱 데이터 아래에 만들어 잠금 인자 그대로 실행한다.
        var mcpLog = Path.Combine(_dir, "mcp.log");
        var wrapper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), mcpLog).OrandactlWrapper(_dir);
        await using var locked = new FakeModelServer([]);
        var tokens = new FakeRunTokens();
        var driver = new CodexDriver(
            tokens,
            new FakeGate(),
            new FixedEndpoint("pipe-x"),
            new AppDataPaths(appData),
            new RecordingLauncher(locked.Port, model, FindFixture("codex-mcp-server.mjs"), mcpLog).AsLauncher(),
            wrapper,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable,
            home,
            TimeSpan.FromMinutes(2));

        var events = await CollectAsync(driver, Request("답하세요."));

        Assert.DoesNotContain(events, e => e is AiError);
        Assert.Empty(Directory.GetFiles(marks)); // MCP·훅 미끼가 하나도 뜨지 않았다
        var lockedBody = string.Concat(locked.Requests);
        Assert.NotEmpty(locked.Requests);
        Assert.DoesNotContain("SKILLBAIT_", lockedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AGENTSBAIT_", lockedBody, StringComparison.Ordinal);
    }

    [CodexEndToEndFact]
    public async Task A_server_that_rejects_the_converted_schema_ends_the_run_with_cli_error_and_no_final_result()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        await using var server = new FakeModelServer([]) { RejectBody = """{"error":{"message":"Invalid schema for response_format 'codex_output_schema': 'required' is required to be supplied and to be an array including every key in properties.","type":"invalid_request_error","param":"text.format.schema","code":"invalid_json_schema"}}""" };
        var tokens = new FakeRunTokens();
        var wrapper = new RecordingLauncher(0, model, FindFixture("codex-mcp-server.mjs"), Path.Combine(_dir, "mcp.log")).OrandactlWrapper(_dir);
        var driver = NewDriver(tokens, new RecordingLauncher(server.Port, model, string.Empty, string.Empty).AsLauncher(), wrapper);

        var events = await CollectAsync(driver, Request("답하세요.") with { OutputSchema = Oranda.Core.Ai.Schemas.TuningResult.Schema() });

        var error = Assert.IsType<AiError>(events[^1]);
        Assert.Equal("cli_error", error.Code);
        Assert.Contains("invalid_json_schema", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is AiFinal);
        Assert.Equal(tokens.Issued, tokens.Revoked);
    }

    /// <summary>
    /// 운영 DB 안전 측정: 느린 게이트 호출이 Codex 포기·사용자 중지·실행 제한 초과에서 어떻게 끝나는지 실제 orandactl·실제 브리지로 잰다.
    /// 게이트 가짜는 두 신호를 따로 기록한다 — 브리지가 준 <c>ct</c>(연결·서버 쪽)와 <c>session.RevocationToken</c>(실제 AgentGate 가 ct 와 묶어 처리자에 넘기는 세션 폐기).
    /// 드라이버 정리는 실행 토큰을 폐기하고(<c>IAgentRunTokens.Revoke</c>) 그것이 세션 폐기로 이어진다. 앱의 흐름은 끝에 <c>session.Revoke()</c> 도 부른다.
    /// 결과는 <c>ORANDA_MEASURE_OUT</c> 파일에 덧붙인다. 개발 빌드 orandactl 은 Codex 가 MCP 자식의 환경을 비워 .NET 을 못 찾으므로 시험 래퍼에서만 DOTNET_ROOT 를 준다(제품은 자체 포함 런타임).
    /// 측정 사실(2026-11-03, codex 0.166.0): 브리지 ct 는 어느 경로에서도 취소되지 않는다(AgentBridgeHost 가 읽기 루프에서 호출을 기다리고 서버 종료 토큰만 넘김) — 이것은 브리지 ct 에 대한 사실이다.
    /// 세션 폐기 신호는 아래 시험이 경로별로 확인한다.
    /// </summary>
    [CodexEndToEndTheory]
    [InlineData("codex-gives-up-after-tool-timeout")]
    [InlineData("codex-gives-up-then-next-call")]
    [InlineData("user-stop")]
    [InlineData("run-time-limit")]
    public async Task A_slow_gate_call_ends_through_the_session_revocation_and_orandactl_exits_on_every_way_a_run_stops(string scenario)
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var gate = new SlowGate();
        var tokens = new FakeRunTokens();
        var unique = Guid.NewGuid().ToString("N")[..10];
        await using var host = new AgentBridgeHost(gate, tokens, (_, instance) => $"oranda-cx-{unique}" + (instance is null ? string.Empty : "-" + instance));
        host.Start();
        var wrapper = Path.Combine(_dir, "orandactl-with-dotnet");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? root : Path.GetDirectoryName(FindDotnet())!;
        File.WriteAllText(wrapper, $"#!/bin/sh\nDOTNET_ROOT=\"{dotnetRoot}\" exec \"{FindOrandactl()}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var secondCall = scenario == "codex-gives-up-then-next-call";
        await using var server = new FakeModelServer(
            secondCall
                ? [McpCall("describe_object", """{"object":"EMP"}"""), McpCall("describe_object", """{"object":"DEPT"}""")]
                : [McpCall("describe_object", """{"object":"EMP"}""")]);
        var launcher = new RecordingLauncher(server.Port, model, string.Empty, string.Empty)
        {
            Tail = scenario.StartsWith("codex-gives-up", StringComparison.Ordinal) ? ["-c", "mcp_servers.orandactl.tool_timeout_sec=4"] : [],
        }.AsLauncher();
        var limit = scenario == "run-time-limit" ? TimeSpan.FromSeconds(6) : TimeSpan.FromMinutes(5);
        var driver = new CodexDriver(
            tokens, gate, host, new AppDataPaths(_dir), launcher, wrapper,
            () => Environment.GetEnvironmentVariable("PATH"), File.Exists, CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable, _dir, limit);

        using var stop = new CancellationTokenSource();
        var events = new List<(DateTime At, AiEvent Event)>();
        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in driver.RunAsync(Request("답하세요."), stop.Token))
                {
                    events.Add((DateTime.UtcNow, e));
                }
            }
            catch (OperationCanceledException)
            {
                // 사용자 중지
            }
        });

        Assert.True(await gate.WaitForStartAsync(TimeSpan.FromSeconds(60)), "게이트 호출이 시작되지 않았다(orandactl 이 못 떴을 수 있다).");
        var startedAt = DateTime.UtcNow;
        double? stopAt = null;
        if (scenario == "user-stop")
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            stopAt = (DateTime.UtcNow - startedAt).TotalSeconds;
            await stop.CancelAsync();
        }

        await run.WaitAsync(TimeSpan.FromMinutes(4));
        var runEndedAt = (DateTime.UtcNow - startedAt).TotalSeconds;
        await gate.WaitForEndAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(3));
        var alive = CountProcesses($"oranda-cx-{unique}");
        string F(double? v) => v?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "NONE";
        var results = events.Where(x => x.Event is AiToolResult).Select(x => (x.At - startedAt).TotalSeconds).ToList();
        var last = events.LastOrDefault().Event;
        var line = $"{scenario}: bridge-ct-cancel={F(gate.BridgeCancelAfter(startedAt))}s session-revoke-signal={F(gate.SessionRevokeAfter(startedAt))}s "
            + $"stop-at={F(stopAt)}s run-ended-at={F(runEndedAt)}s gate-calls={gate.StartCount} tool-result-times=[{string.Join(",", results.Select(r => F(r)))}] "
            + $"orandactl-alive-3s-after={alive} last-event={last?.GetType().Name}:{(last as AiError)?.Code}";
        if (Environment.GetEnvironmentVariable("ORANDA_MEASURE_OUT") is { Length: > 0 } outFile)
        {
            File.AppendAllText(outFile, line + "\n");
        }

        // 브리지 ct 는 취소되지 않는다(위 측정 사실). 세션 폐기 신호가 처리자에 닿아 호출이 끝난다.
        Assert.NotNull(gate.SessionRevokeAfter(startedAt));
        Assert.Equal(0, alive);
        Assert.Equal(tokens.Issued, tokens.Revoked);
        if (scenario == "run-time-limit")
        {
            Assert.Equal("cli_timeout", Assert.IsType<AiError>(last).Code);
        }
    }

    private static int CountProcesses(string commandLineFragment)
    {
        var info = new ProcessStartInfo("pgrep") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(commandLineFragment);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string FindDotnet() => FindExecutable("dotnet");

    private static string FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{name} 이(가) PATH 에 없다.");
    }

    private static string FindOrandactl()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Oranda.Agent.Cli", "bin", "Debug", "net10.0", "orandactl");
    }

    /// <summary>호출이 오래 걸리는 게이트: 브리지 ct 취소와 세션 폐기 신호(<c>RevocationToken</c>)를 각각 시각과 함께 기록한다. 둘 중 하나가 취소되면 호출이 끝난다(실제 AgentGate 처럼 묶어 쓴다).</summary>
    private sealed class SlowGate : IAgentGate
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTime? _bridgeAt;
        private DateTime? _sessionAt;
        private int _startCount;

        public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted
        {
            add { }
            remove { }
        }

        public int StartCount => _startCount;

        public double? BridgeCancelAfter(DateTime origin) => _bridgeAt is { } at ? (at - origin).TotalSeconds : null;

        public double? SessionRevokeAfter(DateTime origin) => _sessionAt is { } at ? (at - origin).TotalSeconds : null;

        public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
            [new AgentToolInfo("describe_object", "slow test tool", JsonNode.Parse("""{"type":"object","properties":{"object":{"type":"string"}}}""")!)];

        public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
        {
            Interlocked.Increment(ref _startCount);
            _started.TrySetResult();
            using var bridge = ct.Register(() => _bridgeAt ??= DateTime.UtcNow);
            using var revoked = session.RevocationToken.Register(() => _sessionAt ??= DateTime.UtcNow);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.RevocationToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(120), linked.Token);
            }
            catch (OperationCanceledException)
            {
                _ended.TrySetResult();
                throw;
            }

            _ended.TrySetResult();
            return new AgentGateResponse(new AgentCallToolResult(false, "{}"), AiToolJudgement.Allowed);
        }

        public async Task<bool> WaitForStartAsync(TimeSpan limit) => await Task.WhenAny(_started.Task, Task.Delay(limit)) == _started.Task;

        public async Task WaitForEndAsync(TimeSpan limit) => await Task.WhenAny(_ended.Task, Task.Delay(limit));
    }

    /// <summary>
    /// 도구 호출 하나가 Codex 의 기본 도구 제한(0.166.0 에서 300초 — 측정)보다 오래 걸려도, 잠금 인자의 <c>tool_timeout_sec</c>(6시간)이 있으므로 끊기지 않고 성공한다.
    /// 약 5분 걸리는 시험이라 <c>ORANDA_AI_E2E_CODEX=1</c> 일 때만 돈다(시간을 줄일 방법이 없다: 기본 제한 자체를 넘어야 의미가 있다).
    /// </summary>
    [CodexEndToEndFact]
    public async Task A_tool_call_longer_than_the_codex_default_limit_succeeds_because_the_lock_sets_the_tool_timeout()
    {
        var model = await PickModelWithBuiltinToolsAsync();
        var mcpLog = Path.Combine(_dir, "slow-mcp.log");
        var wrapper = Path.Combine(_dir, "orandactl-slow");
        File.WriteAllText(wrapper, $"#!/bin/sh\nexec \"{FindExecutable("node")}\" \"{FindFixture("codex-mcp-slow.mjs")}\" \"{mcpLog}\" 305 \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using var server = new FakeModelServer([McpCall("slow_tool", "{}")]);
        var tokens = new FakeRunTokens();
        var gate = new FakeGate();
        var driver = NewDriver(tokens, new RecordingLauncher(server.Port, model, string.Empty, string.Empty) { OnStart = () => RaiseAllowed(gate, "slow_tool") }.AsLauncher(), wrapper, TimeSpan.FromMinutes(8), gate);

        var started = DateTime.UtcNow;
        var events = await CollectLongAsync(driver, Request("slow_tool 을 부르세요."));

        var detail = " 사건: " + string.Join(" | ", events.Select(e => e.ToString()![..Math.Min(160, e.ToString()!.Length)])) + " || mcp: " + (File.Exists(mcpLog) ? File.ReadAllText(mcpLog) : "(없음)");
        Assert.True((DateTime.UtcNow - started).TotalSeconds >= 300, "호출이 기본 제한(300초)보다 오래 걸리지 않았다." + detail);
        Assert.DoesNotContain(events, e => e is AiError);
        var result = Assert.Single(events.OfType<AiToolResult>());
        Assert.NotEqual(AiToolJudgement.Failed, result.Judgement);
        Assert.IsType<AiFinal>(events[^1]);
        Assert.Contains("\"call-answering\"", File.ReadAllText(mcpLog), StringComparison.Ordinal);
    }

    private static async Task<List<AiEvent>> CollectLongAsync(CodexDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        await foreach (var e in driver.RunAsync(request, timeout.Token))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>시험용 MCP 서버는 게이트를 지나지 않으므로, 실제 게이트가 내는 것과 같은 허용 판단 사건을 대신 낸다. 판단 없는 호출은 성공으로 세지 않는다.</summary>
    private static void RaiseAllowed(FakeGate gate, string tool) =>
        gate.Raise(new AgentToolActivityEventArgs("e2e-codex", 1, tool, AiToolJudgement.Allowed, 1, null));

    private CodexDriver NewDriver(FakeRunTokens tokens, IClaudeProcessLauncher launcher, string orandactl, TimeSpan? timeout = null, FakeGate? gate = null) =>
        new(
            tokens,
            gate ?? new FakeGate(),
            new FixedEndpoint("pipe-x"),
            new AppDataPaths(_dir),
            launcher,
            orandactl,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            CodexModelCatalog.RunDebugModelsAsync,
            Environment.GetEnvironmentVariable,
            _dir,
            timeout ?? TimeSpan.FromMinutes(2));

    private static AiRequest Request(string prompt, string? resume = null)
    {
        var profile = new ConnectionProfile("p", "u", "h/s");
        var context = new AiContext("p", null, AiAccessLevel.Plan);
        return new AiRequest(prompt, context, ResumeId: resume, Session: new AgentSession(new AgentRunScope("e2e-codex", profile, context)));
    }

    private static async Task<List<AiEvent>> CollectAsync(CodexDriver driver, AiRequest request)
    {
        var events = new List<AiEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await foreach (var e in driver.RunAsync(request, timeout.Token))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>요청 본문에서 선언된 도구를 모은다: 최상위 tools 와 input 안의 additional_tools. 이름이 없는 항목은 type 으로 적는다.</summary>
    internal static List<string> DeclaredTools(string body)
    {
        var root = JsonNode.Parse(body)!;
        var names = new List<string>();
        void Collect(JsonNode? entries)
        {
            if (entries is not JsonArray array)
            {
                return;
            }

            foreach (var entry in array.OfType<JsonObject>())
            {
                var name = entry["name"]?.GetValue<string>();
                if (entry["tools"] is JsonArray children)
                {
                    names.Add(name ?? "(namespace)");
                    foreach (var child in children.OfType<JsonObject>())
                    {
                        names.Add((name ?? "(namespace)") + "." + (child["name"]?.GetValue<string>() ?? child["type"]?.GetValue<string>() ?? "?"));
                    }
                }
                else
                {
                    names.Add(name ?? "type:" + (entry["type"]?.GetValue<string>() ?? "?"));
                }
            }
        }

        Collect(root["tools"]);
        if (root["input"] is JsonArray input)
        {
            foreach (var item in input.OfType<JsonObject>().Where(i => i["type"]?.GetValue<string>() == "additional_tools"))
            {
                Collect(item["tools"]);
            }
        }

        return names;
    }

    /// <summary>허용 목록 밖 도구: 이름 없는 내장 도구(type:...)와 orandactl 밖 네임스페이스·허용 밖 이름.</summary>
    internal static List<string> Violations(IEnumerable<string> declared) =>
        [.. declared.Where(n => !IsAllowed(n))];

    // 허용 도구가 「functions」 네임스페이스 안에 들어 오는 형태도 있으므로 그 접두사는 벗기고 본다.
    private static bool IsAllowed(string name) =>
        name == "functions"
        || AllowedBare.Contains(name.StartsWith("functions.", StringComparison.Ordinal) ? name["functions.".Length..] : name)
        || name.StartsWith("mcp__orandactl", StringComparison.Ordinal);

    /// <summary>
    /// 앱 커넥터(<c>mcp__codex_apps__*</c>) 도구가 없다. 요청 본문에 <c>codex_apps</c> 라는 글자 자체는 남는다: 시스템 지시문의 「# Apps (Connectors)」 절이
    /// 모델 카탈로그의 고정 설명문(<c>include_apps_usage_instructions</c>)으로 들어 오기 때문이며 도구가 아니다. 그래서 도구 선언과 <c>mcp__codex_apps</c> 이름만 검사한다.
    /// 빈 임시 CODEX_HOME 에는 설치된 앱이 없어 <c>features.apps=false</c> 를 뺀 대조군을 만들 수 없다(부정 단언만 가능).
    /// </summary>
    private static void AssertNoCodexApps(string body)
    {
        Assert.DoesNotContain("mcp__codex_apps", body, StringComparison.Ordinal);
        Assert.DoesNotContain(DeclaredTools(body), n => n.Contains("codex_apps", StringComparison.Ordinal));
    }

    private static IEnumerable<string> FunctionOutputs(string body) =>
        (JsonNode.Parse(body)!["input"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["type"]?.GetValue<string>() is "function_call_output" or "custom_tool_call_output")
            .Select(i => i["output"]?.ToJsonString() ?? string.Empty);

    private static JsonObject Function(string name, string arguments) => new() { ["type"] = "function_call", ["name"] = name, ["arguments"] = arguments };

    private static JsonObject Custom(string name, string input) => new() { ["type"] = "custom_tool_call", ["name"] = name, ["input"] = input };

    private static JsonObject McpCall(string tool, string arguments) =>
        new() { ["type"] = "function_call", ["namespace"] = "mcp__orandactl", ["name"] = tool, ["arguments"] = arguments };

    private static async Task<string> PickModelWithBuiltinToolsAsync()
    {
        var (output, error) = await CodexModelCatalog.RunDebugModelsAsync(FindCodex(), Path.GetTempPath(), CancellationToken.None);
        Assert.Null(error);
        var models = JsonNode.Parse(output!)!["models"]!.AsArray().OfType<JsonObject>();
        var pick = models.FirstOrDefault(m => m["tool_mode"] is not null || m["apply_patch_tool_type"] is not null);
        return Assert.IsType<string>(pick?["slug"]?.GetValue<string>() ?? throw new InvalidOperationException("내장 도구를 가진 모델이 목록에 없어 이 시험이 뜻이 없다."));
    }

    private static async Task<string> LockedCatalogAsync(string runDirectory)
    {
        var (output, error) = await CodexModelCatalog.RunDebugModelsAsync(FindCodex(), runDirectory, CancellationToken.None);
        Assert.Null(error);
        var (json, lockError) = CodexModelCatalog.Lock(output!);
        Assert.Null(lockError);
        var path = Path.Combine(runDirectory, "model-catalog.json");
        File.WriteAllText(path, json!);
        return path;
    }

    private static List<string> ControlArguments(int port, string model) =>
        new RecordingLauncher(port, model, string.Empty, string.Empty).WithFakeProvider(
            ["exec", "--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "-s", "read-only", "-"]);

    /// <param name="killAfter">주어지면 이 시간 뒤 프로세스 트리를 강제 종료한다(Oranda 의 정리가 SIGKILL 이라 정상 종료 때 지워지는 파일이 남는 경로를 재현한다).</param>
    private static async Task RunRawAsync(IReadOnlyList<string> arguments, FakeModelServer server, string prompt, string? workingDirectory = null, TimeSpan? killAfter = null)
    {
        var info = new ProcessStartInfo(FindCodex())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["ORANDA_RUN_TOKEN"] = "fake-token-not-real";
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteAsync(prompt + "\n");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(killAfter ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (killAfter is not null)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            var tail = (await stdout)[^Math.Min(1500, (await stdout).Length)..] + "\n--stderr--\n" + (await stderr);
            throw new TimeoutException("codex 가 60초 안에 끝나지 않았다: " + tail);
        }

        _ = await stdout;
        _ = await stderr;
    }

    private static string FindCodex()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "codex");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("codex 가 PATH 에 없다.");
    }

    private static string FindFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "Oranda.Platform.Tests", "Ai", "Cli", "Fixtures", name);
    }

    /// <summary>가짜 모델 서버 주소·모델 지정(<c>-c</c> 설정)과 orandactl 자리 MCP 서버(node 스크립트)를 만든다.</summary>
    private sealed class RecordingLauncher(int port, string model, string mcpScript, string mcpLog)
    {
        /// <summary>잠금 인자 뒤(마지막 "-" 앞)에 덧붙일 인자. 같은 설정 키는 뒤의 값이 이긴다.</summary>
        public string[] Tail { get; init; } = [];

        /// <summary>프로세스를 시작할 때 부르는 곳: 게이트를 지나지 않는 시험용 MCP 서버를 쓰는 시험이 게이트 판단 사건을 대신 내는 자리.</summary>
        public Action? OnStart { get; init; }

        public List<string> WithFakeProvider(IReadOnlyList<string> arguments)
        {
            // 마지막 인자(표준 입력 표지 "-")와 이어 말하기의 대화 id 앞에 -c 를 끼운다: --ignore-rules 바로 뒤가 두 형태 모두에서 옵션 자리다.
            var list = arguments.ToList();
            var at = list.IndexOf("--ignore-rules") + 1;
            list.InsertRange(at,
            [
                "-c", "model_provider=fake",
                "-c", $"model_providers.fake={{name=\"f\",base_url=\"http://127.0.0.1:{port}/v1\",wire_api=\"responses\",env_key=\"OPENAI_API_KEY\"}}",
                "-c", $"model=\"{model}\"",
            ]);
            return list;
        }

        public string OrandactlWrapper(string directory)
        {
            var path = Path.Combine(directory, "orandactl-fake");
            File.WriteAllText(path, $"#!/bin/sh\nexec node \"{mcpScript}\" \"{mcpLog}\" \"$@\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        public IClaudeProcessLauncher AsLauncher() => new ExtraArgumentsLauncher(this);

        private sealed class ExtraArgumentsLauncher(RecordingLauncher owner) : IClaudeProcessLauncher
        {
            private readonly SystemClaudeProcessLauncher _inner = new();

            public IClaudeProcess Start(ClaudeLaunch launch)
            {
                owner.OnStart?.Invoke();
                var arguments = owner.WithFakeProvider(launch.Arguments);
                arguments.InsertRange(arguments.Count - 1, owner.Tail);
                return _inner.Start(launch with { Arguments = arguments });
            }
        }
    }

    /// <summary>Responses API 흉내: 요청마다 대본 항목을 하나씩 내고 끝나면 글로 마친다.</summary>
    private sealed class FakeModelServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly object _lock = new();
        private List<JsonObject> _script;
        private int _step;
        private int _counter;

        public FakeModelServer(IEnumerable<JsonObject> script)
        {
            _script = [.. script];
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public int Port { get; }

        /// <summary>응답 전 지연. 스냅숏 파일처럼 비동기로 쓰는 부수 효과가 실행 종료 전에 끝나도록 대조 실행에서만 준다.</summary>
        public TimeSpan Delay { get; init; }

        /// <summary>대본이 끝난 뒤(최종 글 응답)에만 주는 지연. 도구 호출은 바로 응답하고 마지막에서 멈춰 강제 종료 경로를 만든다.</summary>
        public TimeSpan FinalDelay { get; init; }

        /// <summary>null 이 아니면 모든 요청을 이 JSON 본문의 HTTP 400 으로 거부한다(서버가 변환한 스키마를 거부하는 경우 흉내).</summary>
        public string? RejectBody { get; init; }

        public List<string> Requests { get; } = [];

        public void Reset(IEnumerable<JsonObject> script)
        {
            lock (_lock)
            {
                _script = [.. script];
                _step = 0;
                Requests.Clear();
            }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                JsonObject item;
                bool isFinal;
                lock (_lock)
                {
                    isFinal = _step >= _script.Count;
                    if (context.Request.HttpMethod == "POST")
                    {
                        Requests.Add(body);
                    }

                    _counter++;
                    item = _step < _script.Count
                        ? (JsonObject)_script[_step++].DeepClone()
                        : new JsonObject
                        {
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "probe-done", ["annotations"] = new JsonArray() }),
                        };
                    item["id"] = "it_" + _counter;
                    item["status"] = "completed";
                    if (item["type"]?.GetValue<string>() is "function_call" or "custom_tool_call")
                    {
                        item["call_id"] = "call_" + _counter;
                    }
                }

                if (RejectBody is not null)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(RejectBody));
                    context.Response.Close();
                    continue;
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay);
                }

                if (isFinal && FinalDelay > TimeSpan.Zero)
                {
                    await Task.Delay(FinalDelay);
                }

                context.Response.ContentType = "text/event-stream";
                var id = "resp_" + _counter;
                var sse = new StringBuilder();
                void Event(string type, JsonObject payload)
                {
                    payload["type"] = type;
                    sse.Append("event: ").Append(type).Append("\ndata: ").Append(payload.ToJsonString()).Append("\n\n");
                }

                Event("response.created", new JsonObject { ["response"] = new JsonObject { ["id"] = id } });
                Event("response.output_item.added", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
                Event("response.output_item.done", new JsonObject { ["output_index"] = 0, ["item"] = item.DeepClone() });
                Event("response.completed", new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["id"] = id,
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"] = 1,
                            ["output_tokens"] = 1,
                            ["total_tokens"] = 2,
                            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                        },
                    },
                });
                var bytes = Encoding.UTF8.GetBytes(sse.ToString());
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await Task.WhenAny(_loop, Task.Delay(1000));
        }
    }
}

public sealed class CodexEndToEndTheoryAttribute : TheoryAttribute
{
    public CodexEndToEndTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E_CODEX") != "1")
        {
            Skip = "ORANDA_AI_E2E_CODEX=1 일 때만 설치된 codex 와 가짜 모델 서버로 잠금을 확인한다.";
        }
    }
}

public sealed class CodexEndToEndFactAttribute : FactAttribute
{
    public CodexEndToEndFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_AI_E2E_CODEX") != "1")
        {
            Skip = "ORANDA_AI_E2E_CODEX=1 일 때만 설치된 codex 와 가짜 모델 서버로 잠금을 확인한다.";
        }
    }
}
