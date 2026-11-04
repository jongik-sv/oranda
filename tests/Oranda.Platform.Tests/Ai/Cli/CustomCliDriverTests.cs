using System.ComponentModel;
using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>
/// 기타 CLI 드라이버. 프로세스는 <see cref="FakeLauncher"/> 로 대신하지만 넘기는 값은 운영 경로와 같은 <see cref="ClaudeLaunch"/> 이다
/// (환경 적용은 실제 <see cref="SystemClaudeProcessLauncher"/> 의 함수로도 확인한다).
/// </summary>
public sealed class CustomCliDriverTests : IDisposable
{
    // Windows 는 이름.exe 를 찾으므로(SystemCliExecutable) 대역 실행 파일 이름도 맞춘다.
    private static readonly string FakeExe = Path.Combine("/fake/bin", OperatingSystem.IsWindows() ? "mycli.exe" : "mycli");

    // 실행 파일 폴더 기대값은 실제 코드와 같은 API 로 만든다(Windows 의 GetDirectoryName 은 구분자를 바꾼다).
    private static readonly string FakeDir = Path.GetDirectoryName(FakeExe)!;

    private static readonly AiContext Context = new("p", null, AiAccessLevel.Plan);

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","required":["a"],"properties":{"a":{"type":"string"}}}""").RootElement.Clone();

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "oranda-custom-cli-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLauncher _launcher = new();
    private string[] _environmentNames = [];

    public CustomCliDriverTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private CustomCliDriver Create(TimeSpan? timeout = null) =>
        new(_launcher, () => "/fake/bin", path => path == FakeExe, () => _environmentNames, "/home/x", _temp, timeout ?? TimeSpan.FromMinutes(1));

    private static AiCustomCli Cli(string command = "mycli --quiet") => new() { Id = "c1", Name = "사내 CLI", Command = command };

    private static AiRequest Request(string prompt = "질문", string? resumeId = null, JsonElement? schema = null) =>
        new(prompt, Context, resumeId, schema, MaskingRequired: false);

    private static async Task<List<AiEvent>> RunAsync(CustomCliDriver driver, AiRequest request, AiCustomCli cli, CancellationToken ct = default)
    {
        var events = new List<AiEvent>();
        await foreach (var e in driver.RunAsync(request, cli, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static AiError ErrorOf(IReadOnlyList<AiEvent> events) => Assert.IsType<AiError>(events[^1]);

    [Fact]
    public async Task The_prompt_goes_to_standard_input_by_default_in_an_empty_temp_folder_that_is_removed_afterwards()
    {
        _launcher.Lines = ["첫 줄", "둘째 줄"];
        var existedDuringRun = false;
        _launcher.OnStart = launch => existedDuringRun = Directory.Exists(launch.WorkingDirectory) && !Directory.EnumerateFileSystemEntries(launch.WorkingDirectory).Any();

        var events = await RunAsync(Create(), Request("이 SQL 을 설명해 줘"), Cli());

        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.StartsWith("첫 줄", final.Text);
        Assert.Contains("둘째 줄", final.Text);
        Assert.NotNull(final.ResumeId);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(FakeExe, launch.ExecutablePath);
        Assert.Equal(["--quiet"], launch.Arguments);
        Assert.Contains("이 SQL 을 설명해 줘", launch.StandardInput);
        Assert.True(existedDuringRun);
        Assert.StartsWith(_temp, launch.WorkingDirectory);
        Assert.False(Directory.Exists(launch.WorkingDirectory));
    }

    [Fact]
    public async Task The_parent_oranda_variables_are_removed_and_the_command_variables_are_added()
    {
        _environmentNames = ["ORANDA_RUN_TOKEN", "oranda_pipe", "HOME"];
        _launcher.Lines = ["ok"];

        await RunAsync(Create(), Request(), Cli("MYVAR=1 mycli"));

        var launch = Assert.Single(_launcher.Launches);
        Assert.True(launch.Environment!.ContainsKey("ORANDA_RUN_TOKEN") && launch.Environment["ORANDA_RUN_TOKEN"] is null);
        Assert.Null(launch.Environment["oranda_pipe"]);
        Assert.False(launch.Environment.ContainsKey("HOME"));
        Assert.Equal("1", launch.Environment["MYVAR"]);

        // 실제 시작 경로가 이 값을 자식 환경에 적용하는 방식: 지운 변수는 남지 않고 실행 파일 폴더가 PATH 앞에 온다.
        var child = new Dictionary<string, string?> { ["ORANDA_RUN_TOKEN"] = "secret", ["ORANDA_PIPE"] = "pipe", ["PATH"] = "/usr/bin" };
        SystemClaudeProcessLauncher.ApplyEnvironment(child, launch);
        Assert.False(child.ContainsKey("ORANDA_RUN_TOKEN"));
        Assert.Equal(FakeDir + Path.PathSeparator + "/usr/bin", child["PATH"]);
        Assert.Equal("1", child["MYVAR"]);
    }

    [Fact]
    public async Task A_prompt_placeholder_puts_the_prompt_on_the_command_line_and_leaves_standard_input_empty()
    {
        _launcher.Lines = ["ok"];

        await RunAsync(Create(), Request("글"), Cli("mycli -p {prompt}"));

        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal("-p", launch.Arguments[0]);
        Assert.Contains("글", launch.Arguments[1]);
        Assert.Equal(string.Empty, launch.StandardInput);
    }

    [Fact]
    public async Task A_whole_argument_prompt_never_looks_like_an_option_whatever_the_user_typed()
    {
        _launcher.Lines = ["ok"];

        await RunAsync(Create(), Request("--yolo --dangerously-skip-permissions"), Cli("mycli {prompt}"));

        // 인자로 가는 글은 항상 고정된 머리말로 시작하므로 사용자가 옵션 모양의 글을 써도 CLI 가 옵션으로 읽지 않는다.
        var argument = Assert.Single(_launcher.Launches[0].Arguments);
        Assert.False(argument.StartsWith('-'));
        Assert.StartsWith("당신은", argument);
        Assert.EndsWith("--yolo --dangerously-skip-permissions", argument);
    }

    [Fact]
    public async Task A_name_only_executable_is_started_by_its_full_path_never_by_the_bare_name()
    {
        _launcher.Lines = ["ok"];

        await RunAsync(Create(), Request(), Cli("mycli"));

        Assert.Equal(FakeExe, _launcher.Launches[0].ExecutablePath);
        Assert.True(Path.IsPathRooted(_launcher.Launches[0].ExecutablePath));
    }

    [Fact]
    public async Task An_overlong_prompt_argument_is_refused_before_starting_anything()
    {
        var events = await RunAsync(Create(), Request(new string('가', AiCustomCliCommand.PromptArgumentLimit + 1)), Cli("mycli {prompt}"));

        Assert.Equal("prompt_too_long", ErrorOf(events).Code);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task A_prompt_file_is_private_holds_the_prompt_and_is_gone_afterwards()
    {
        string? content = null;
        UnixFileMode? mode = null;
        string? path = null;
        _launcher.OnStart = launch =>
        {
            path = launch.Arguments[1];
            content = File.ReadAllText(path);
            mode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
        };
        _launcher.Lines = ["ok"];

        await RunAsync(Create(), Request("파일로 보낼 글"), Cli("mycli --attach {prompt_file}"));

        Assert.Contains("파일로 보낼 글", content);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        Assert.False(File.Exists(path));
        Assert.Equal(string.Empty, _launcher.Launches[0].StandardInput);
    }

    [Fact]
    public async Task A_schema_is_attached_to_the_prompt_and_the_json_in_a_code_block_becomes_the_result()
    {
        _launcher.Lines = ["설명입니다.", "```json", """{"a":"값"}""", "```"];

        var events = await RunAsync(Create(), Request(schema: Schema), Cli());

        var final = Assert.IsType<AiFinal>(events[^1]);
        Assert.Equal("값", final.Result!.Value.GetProperty("a").GetString());
        Assert.Null(final.Text);
        Assert.Contains("\"required\"", _launcher.Launches[0].StandardInput);
    }

    [Fact]
    public async Task A_candidate_with_a_duplicate_key_is_skipped_and_the_next_candidate_is_used()
    {
        _launcher.Lines = ["```json", """{"a":"맞음"}""", "```", "```json", """{"a":1,"a":2}""", "```"];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(schema: Schema), Cli()))[^1]);

        Assert.Equal("맞음", final.Result!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public async Task The_last_object_that_fits_the_schema_wins_over_other_braces_in_the_text()
    {
        _launcher.Lines = ["""앞 {"a":1} 뒤 {"a":"맞음"} 끝 {불완전"""];

        var events = await RunAsync(Create(), Request(schema: Schema), Cli());

        Assert.Equal("맞음", Assert.IsType<AiFinal>(events[^1]).Result!.Value.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData("그냥 글입니다", false)]
    [InlineData("""{"b":"x"}""", true)]
    public async Task A_missing_or_mismatching_json_is_reported_with_the_text_the_cli_wrote(string output, bool found)
    {
        _launcher.Lines = [output];

        var events = await RunAsync(Create(), Request(schema: Schema), Cli());

        var error = ErrorOf(events);
        Assert.Equal(CustomCliDriver.StructuredInvalidCode, error.Code);
        Assert.Contains(output, error.Message);
        Assert.Equal(found, error.Message.Contains("필수 칸", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e is AiFinal);
    }

    [Fact]
    public async Task A_non_zero_exit_code_is_an_error_with_the_cleaned_standard_error()
    {
        _launcher.ExitCode = 3;
        _launcher.StandardError = "\u001b[31mboom\u001b[0m";
        _launcher.Lines = ["부분 출력"];

        var error = ErrorOf(await RunAsync(Create(), Request(), Cli()));

        Assert.Equal("custom_cli_failed", error.Code);
        Assert.Contains("3", error.Message);
        Assert.Contains("boom", error.Message);
        Assert.DoesNotContain("\u001b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_node_gets_a_hint_about_the_wrapper()
    {
        _launcher.ExitCode = 127;
        _launcher.StandardError = "env: node: No such file or directory";

        var error = ErrorOf(await RunAsync(Create(), Request(), Cli()));

        Assert.Contains("래퍼", error.Message);
    }

    [Fact]
    public async Task Ansi_sequences_are_removed_from_the_output()
    {
        _launcher.Lines = ["\u001b[1;32m굵게\u001b[0m 글"];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(), Cli()))[^1]);

        Assert.Equal("굵게 글", final.Text);
    }

    [Fact]
    public async Task An_oversized_output_keeps_the_end_and_says_the_beginning_was_dropped()
    {
        _launcher.Lines = ["앞부분", .. Enumerable.Repeat(new string('x', 1000), 2500), "끝부분"];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(), Cli()))[^1]);

        Assert.StartsWith("(출력이 너무 길어 앞부분을 생략했습니다.)", final.Text);
        Assert.EndsWith("끝부분", final.Text);
        Assert.DoesNotContain("앞부분\n", final.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_structured_result_at_the_end_survives_an_oversized_output()
    {
        _launcher.Lines = [.. Enumerable.Repeat(new string('x', 1000), 2500), """{"a":"끝"}"""];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(schema: Schema), Cli()))[^1]);

        Assert.Equal("끝", final.Result!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public async Task A_code_block_of_another_language_does_not_hide_the_json_block_after_it()
    {
        _launcher.Lines = ["```sql", "select 1 from dual;", "```", "설명", "```json", """{"a":"블록"}""", "```"];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(schema: Schema), Cli()))[^1]);

        Assert.Equal("블록", final.Result!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public async Task Many_braces_before_the_answer_do_not_push_the_final_json_out()
    {
        _launcher.Lines = [string.Concat(Enumerable.Repeat("{ ", 500)), "그리고 결과:", """{"a":"뒤"}"""];

        var final = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(schema: Schema), Cli()))[^1]);

        Assert.Equal("뒤", final.Result!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public async Task An_unterminated_code_block_flood_is_searched_in_linear_time()
    {
        _launcher.Lines = [.. Enumerable.Repeat("x```", 100_000)];

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = ErrorOf(await RunAsync(Create(), Request(schema: Schema), Cli()));

        Assert.Equal(CustomCliDriver.StructuredInvalidCode, error.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"{stopwatch.Elapsed}");
    }

    [Fact]
    public async Task An_echoed_prompt_does_not_make_the_schema_or_an_earlier_result_the_answer()
    {
        var driver = Create();
        _launcher.Lines = ["""{"a":"앞 답"}"""];
        var first = Assert.IsType<AiFinal>((await RunAsync(driver, Request("첫 질문", schema: Schema), Cli()))[^1]);

        // 입력을 그대로 되풀이하는 CLI: 출력에 프롬프트(스키마·앞 답 포함)만 나오고 이번 답은 없다.
        _launcher.OnStart = launch => _launcher.Lines = [launch.StandardInput];
        var error = ErrorOf(await RunAsync(driver, Request("둘째 질문", first.ResumeId, Schema), Cli()));

        Assert.Equal(CustomCliDriver.StructuredInvalidCode, error.Code);
    }

    [Fact]
    public async Task A_loose_schema_echoed_back_is_not_taken_as_the_result()
    {
        var loose = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        _launcher.Lines = ["""{"type":"object"}"""];

        var error = ErrorOf(await RunAsync(Create(), Request(schema: loose), Cli()));

        Assert.Equal(CustomCliDriver.StructuredInvalidCode, error.Code);
    }

    [Fact]
    public async Task An_empty_output_is_an_error_not_an_empty_answer()
    {
        var error = ErrorOf(await RunAsync(Create(), Request(), Cli()));

        Assert.Equal("custom_cli_empty_output", error.Code);
    }

    [Fact]
    public async Task Cancelling_ends_the_process_and_removes_the_temp_folder()
    {
        _launcher.Blocking = true;
        using var cts = new CancellationTokenSource();
        var run = RunAsync(Create(), Request(), Cli(), cts.Token);
        await _launcher.Started.Task;

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(_launcher.Process!.Killed);
        Assert.False(Directory.Exists(_launcher.Launches[0].WorkingDirectory));
    }

    [Fact]
    public async Task Exceeding_the_run_limit_ends_the_process_tree_and_reports_a_timeout()
    {
        _launcher.Blocking = true;

        var events = await RunAsync(Create(TimeSpan.FromMilliseconds(80)), Request(), Cli());

        Assert.Equal("custom_cli_timeout", ErrorOf(events).Code);
        Assert.True(_launcher.Process!.Killed);
    }

    [Fact]
    public async Task A_resumed_conversation_carries_the_earlier_exchange_and_a_failed_run_leaves_no_trace()
    {
        var driver = Create();
        _launcher.Lines = ["첫 답"];
        var first = Assert.IsType<AiFinal>((await RunAsync(driver, Request("첫 질문"), Cli()))[^1]);

        _launcher.ExitCode = 1;
        _launcher.Lines = [];
        await RunAsync(driver, Request("실패할 질문", first.ResumeId), Cli());

        _launcher.ExitCode = 0;
        _launcher.Lines = ["둘째 답"];
        var second = Assert.IsType<AiFinal>((await RunAsync(driver, Request("둘째 질문", first.ResumeId), Cli()))[^1]);

        var input = _launcher.Launches[^1].StandardInput;
        Assert.Equal(first.ResumeId, second.ResumeId);
        Assert.Contains("첫 질문", input);
        Assert.Contains("첫 답", input);
        Assert.Contains("둘째 질문", input);
        Assert.DoesNotContain("실패할 질문", input);
    }

    [Fact]
    public async Task A_conversation_cannot_be_resumed_after_the_command_changed_or_when_the_id_is_unknown()
    {
        var driver = Create();
        _launcher.Lines = ["답"];
        var first = Assert.IsType<AiFinal>((await RunAsync(driver, Request(), Cli()))[^1]);
        var launches = _launcher.Launches.Count;

        var changed = ErrorOf(await RunAsync(driver, Request(resumeId: first.ResumeId), Cli("mycli --auto-approve")));
        var unknown = ErrorOf(await RunAsync(driver, Request(resumeId: "api-nope"), Cli()));

        Assert.Equal("resume_route_mismatch", changed.Code);
        Assert.Equal("resume_unknown", unknown.Code);
        Assert.Equal(launches, _launcher.Launches.Count);
    }

    [Fact]
    public async Task Images_are_refused_before_starting_anything()
    {
        var request = Request() with { Images = [new AiImageAttachment("image/png", [1, 2, 3])] };

        var error = ErrorOf(await RunAsync(Create(), request, Cli()));

        Assert.Equal("custom_cli_images_unsupported", error.Code);
        Assert.Empty(_launcher.Launches);
    }

    [Theory]
    [InlineData("mycli | tee out", "custom_cli_command_invalid")]
    [InlineData("ORANDA_RUN_TOKEN=x mycli", "custom_cli_command_invalid")]
    [InlineData("./mycli", "custom_cli_command_invalid")]
    [InlineData("no-such-cli", "cli_not_found")]
    [InlineData("", "custom_cli_command_invalid")]
    public async Task A_bad_command_or_a_missing_executable_is_closed_and_starts_nothing(string command, string code)
    {
        var error = ErrorOf(await RunAsync(Create(), Request(), Cli(command)));

        Assert.Equal(code, error.Code);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task A_batch_file_given_by_absolute_path_is_refused()
    {
        var driver = new CustomCliDriver(_launcher, () => "", _ => true, () => [], "/home/x", _temp, TimeSpan.FromMinutes(1));
        var command = OperatingSystem.IsWindows() ? @"C:\tools\mycli.cmd" : "/tools/mycli.cmd";

        var error = ErrorOf(await RunAsync(driver, Request(), Cli(command)));

        Assert.Equal("cli_unsupported", error.Code);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task A_launcher_failure_is_reported_as_a_start_error()
    {
        _launcher.OnStart = _ => throw new Win32Exception("exec format error");

        var error = ErrorOf(await RunAsync(Create(), Request(), Cli()));

        Assert.Equal("custom_cli_start_failed", error.Code);
    }

    [Fact]
    public async Task A_real_process_echoes_the_standard_input_back_and_leaves_no_temp_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 셸 도구가 없다.
        }

        var driver = new CustomCliDriver(new SystemClaudeProcessLauncher(), () => Environment.GetEnvironmentVariable("PATH"), File.Exists, () => [], "/home/x", _temp, TimeSpan.FromSeconds(30));

        var final = Assert.IsType<AiFinal>((await RunAsync(driver, Request("실제 프로세스 왕복"), Cli("/bin/cat")))[^1]);

        Assert.Contains("실제 프로세스 왕복", final.Text);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Cancelling_a_real_run_ends_the_child_of_the_cli_too()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = Path.Combine(_temp, "child.pid");
        var driver = new CustomCliDriver(new SystemClaudeProcessLauncher(), () => Environment.GetEnvironmentVariable("PATH"), File.Exists, () => [], "/home/x", _temp, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        var command = $"/bin/sh -c 'sleep 60 & echo $! > \"{pidFile}\"; wait'";
        var run = RunAsync(driver, Request(), Cli(command), cts.Token);
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(pidFile), "자식 프로세스가 시작되지 않았다");
        var childPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var gone = false;
        for (var i = 0; i < 60 && !gone; i++)
        {
            gone = !IsAlive(childPid);
            if (!gone)
            {
                await Task.Delay(50);
            }
        }

        Assert.True(gone, "취소한 뒤에도 CLI 의 자식 프로세스가 남아 있다");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_temp_folder_that_cannot_be_created_is_an_error_not_an_exception()
    {
        var blocker = Path.Combine(_temp, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "x");
        var driver = new CustomCliDriver(_launcher, () => "/fake/bin", path => path == FakeExe, () => [], "/home/x", blocker, TimeSpan.FromMinutes(1));

        var error = ErrorOf(await RunAsync(driver, Request(), Cli()));

        Assert.Equal("custom_cli_workspace_failed", error.Code);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Every_secret_looking_variable_value_is_masked_in_errors_and_answers()
    {
        _launcher.ExitCode = 2;
        _launcher.StandardError = "key sk-abcdef1234 and tok-98765432 leaked";
        var failure = ErrorOf(await RunAsync(Create(), Request(), Cli("GEMINI_KEY=sk-abcdef1234 OTHER_TOKEN=tok-98765432 mycli")));

        Assert.DoesNotContain("sk-abcdef1234", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-98765432", failure.Message, StringComparison.Ordinal);
        Assert.Contains("***", failure.Message);

        _launcher.ExitCode = 0;
        _launcher.Lines = ["답: sk-abcdef1234"];
        var answer = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(), Cli("GEMINI_KEY=sk-abcdef1234 mycli")))[^1]);
        Assert.DoesNotContain("sk-abcdef1234", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_plain_or_numeric_variable_values_are_not_masked_so_the_answer_stays_readable()
    {
        _launcher.Lines = ["ORCL 에서 AUTH 가 true 이고 포트 1521 입니다. 비밀은 sk-abcdef1234 입니다."];
        var command = "ORACLE_DSN=ORCL AUTH_ENABLED=true DB_PASSWORD_PORT=1521 GEMINI_KEY=sk-abcdef1234 mycli";

        var answer = Assert.IsType<AiFinal>((await RunAsync(Create(), Request(), Cli(command)))[^1]);

        Assert.Equal("ORCL 에서 AUTH 가 true 이고 포트 1521 입니다. 비밀은 *** 입니다.", answer.Text);
    }

    [Fact]
    public async Task A_path_variable_in_the_command_is_used_to_find_the_executable_and_keeps_the_executable_folder_in_front()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _launcher.Lines = ["ok"];
        var driver = new CustomCliDriver(_launcher, () => "/elsewhere", path => path == "/opt/t/bin/mytool" || path == FakeExe, () => [], "/home/x", _temp, TimeSpan.FromMinutes(1));

        await RunAsync(driver, Request(), Cli("PATH=/opt/t/bin mytool"));
        await RunAsync(driver, Request(), Cli($"PATH=/usr/bin {FakeExe}"));

        Assert.Equal("/opt/t/bin/mytool", _launcher.Launches[0].ExecutablePath);
        Assert.Equal("/opt/t/bin", _launcher.Launches[0].Environment!["PATH"]);
        Assert.Equal(FakeDir + Path.PathSeparator + "/usr/bin", _launcher.Launches[1].Environment!["PATH"]);

        // 시작 경로가 명령 변수를 마지막에 적용해도 실행 파일 폴더가 남는다.
        var child = new Dictionary<string, string?> { ["PATH"] = "/parent" };
        SystemClaudeProcessLauncher.ApplyEnvironment(child, _launcher.Launches[1]);
        Assert.Equal(FakeDir + Path.PathSeparator + "/usr/bin", child["PATH"]);
    }

    [Fact]
    public async Task A_huge_output_without_any_line_break_ends_as_a_truncated_answer_instead_of_piling_up()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var driver = new CustomCliDriver(new SystemClaudeProcessLauncher(), () => Environment.GetEnvironmentVariable("PATH"), File.Exists, () => [], "/home/x", _temp, TimeSpan.FromSeconds(60));

        var final = Assert.IsType<AiFinal>((await RunAsync(driver, Request(), Cli("/bin/sh -c 'head -c 6000000 /dev/zero | tr \"\\0\" x'")))[^1]);

        Assert.StartsWith("(출력이 너무 길어", final.Text, StringComparison.Ordinal);
        Assert.InRange(final.Text!.Length, 1_000_000, 2_100_000);
    }

    [Fact]
    public async Task An_answer_is_returned_even_when_a_grandchild_keeps_the_output_pipe_open_after_the_cli_exits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var driver = new CustomCliDriver(new SystemClaudeProcessLauncher(), () => Environment.GetEnvironmentVariable("PATH"), File.Exists, () => [], "/home/x", _temp, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(300));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var final = Assert.IsType<AiFinal>((await RunAsync(driver, Request(), Cli("/bin/sh -c 'echo 응답; sleep 4 &'")))[^1]);

        Assert.Contains("응답", final.Text);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"파이프가 닫히기를 기다렸다: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Nothing_from_the_context_or_the_session_reaches_the_prompt_or_the_environment()
    {
        _environmentNames = ["ORANDA_RUN_TOKEN"];
        _launcher.Lines = ["ok"];
        var context = new AiContext("secret-connection-name", Oranda.Core.Connection.ConnectionEnvironment.Development, AiAccessLevel.QueryRows,
            TabId: "tab-secret", StatementText: "select secret_column from secret_table", SqlId: "sqlid-secret", CurrentSchema: "SECRET_SCHEMA", AllowRiskyFunctions: true);
        var profile = new Oranda.Core.Connection.ConnectionProfile("secret-profile", "user-secret", "host-secret/service-secret");
        var session = new Oranda.Core.Ai.Gate.AgentSession(new Oranda.Core.Ai.Gate.AgentRunScope("run-secret", profile, context));
        var request = new AiRequest("일반 질문", context, Session: session, MaskingRequired: false);

        await RunAsync(Create(), request, Cli());

        var launch = Assert.Single(_launcher.Launches);
        var everything = launch.StandardInput + "\n" + string.Join("\n", launch.Arguments) + "\n" + string.Join("\n", launch.Environment!.Select(p => $"{p.Key}={p.Value}"));
        foreach (var leaked in new[] { "secret-connection-name", "tab-secret", "secret_column", "secret_table", "sqlid-secret", "SECRET_SCHEMA", "secret-profile", "user-secret", "host-secret", "run-secret" })
        {
            Assert.DoesNotContain(leaked, everything, StringComparison.Ordinal);
        }

        Assert.Contains("일반 질문", launch.StandardInput);
        Assert.Null(launch.Environment!["ORANDA_RUN_TOKEN"]);
    }

    [Fact]
    public async Task The_session_is_never_used_and_no_token_is_needed()
    {
        _launcher.Lines = ["ok"];

        // 세션이 없는 요청도 그대로 실행된다(토큰·게이트 의존이 없다).
        var events = await RunAsync(Create(), Request() with { Session = null }, Cli());

        Assert.IsType<AiFinal>(events[^1]);
        Assert.Single(_launcher.Launches);
    }
}
