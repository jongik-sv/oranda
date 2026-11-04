using Oranda.Core.Ai.Profiles;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>기타 CLI 명령 문자열 분해: 따옴표, 환경 변수, 거부 규칙, 자리표시, 표시용 경고.</summary>
public class AiCustomCliCommandTests
{
    private static AiCustomCliCommand Ok(string command)
    {
        var result = AiCustomCliCommand.Parse(command);
        Assert.True(result.Succeeded, $"{command} → {result.Error}");
        return result.Command!;
    }

    private static AiCustomCliCommandError? ErrorOf(string? command) => AiCustomCliCommand.Parse(command).Error;

    [Fact]
    public void Splits_into_an_executable_and_arguments_without_a_shell()
    {
        var command = Ok("mycli --model fast -p");

        Assert.Equal("mycli", command.Executable);
        Assert.Equal(["--model", "fast", "-p"], command.Arguments);
        Assert.Empty(command.Environment);
        Assert.True(command.SendsPromptOnStandardInput);
    }

    [Fact]
    public void Quotes_keep_blanks_and_backslashes_stay_for_windows_paths()
    {
        var executable = OperatingSystem.IsWindows() ? @"C:\Program Files\My Cli\mycli.exe" : "/opt/My Cli/mycli";
        var command = Ok($"\"{executable}\" --note 'a  b' --say \"he said \\\"hi\\\"\" --dir C:\\work");

        Assert.Equal(executable, command.Executable);
        Assert.Equal(["--note", "a  b", "--say", @"he said ""hi""", "--dir", @"C:\work"], command.Arguments);
    }

    [Fact]
    public void Shell_symbols_are_fine_inside_single_quotes_and_pipes_inside_double_quotes()
    {
        var command = Ok("""mycli --a 'x | y; $(z) `w`' --b "p|q;r>s" """);

        Assert.Equal(["--a", "x | y; $(z) `w`", "--b", "p|q;r>s"], command.Arguments);
    }

    [Theory]
    [InlineData("mycli | tee out")]
    [InlineData("mycli; rm x")]
    [InlineData("mycli && x")]
    [InlineData("mycli > out.txt")]
    [InlineData("mycli < in.txt")]
    [InlineData("mycli `date`")]
    [InlineData("mycli $(date)")]
    [InlineData("mycli \"$(date)\"")]
    [InlineData("mycli \"`date`\"")]
    public void Shell_syntax_is_rejected(string command) =>
        Assert.Equal(AiCustomCliCommandError.ShellSyntax, ErrorOf(command));

    [Theory]
    [InlineData(null, AiCustomCliCommandError.Empty)]
    [InlineData("", AiCustomCliCommandError.Empty)]
    [InlineData("   ", AiCustomCliCommandError.Empty)]
    [InlineData("""FOO=bar""", AiCustomCliCommandError.Empty)]
    [InlineData("""mycli "unclosed""", AiCustomCliCommandError.UnterminatedQuote)]
    [InlineData("mycli 'unclosed", AiCustomCliCommandError.UnterminatedQuote)]
    [InlineData("mycli a\nmycli b", AiCustomCliCommandError.ControlCharacter)]
    [InlineData("./mycli --x", AiCustomCliCommandError.RelativeExecutable)]
    [InlineData("bin/mycli", AiCustomCliCommandError.RelativeExecutable)]
    [InlineData(@"..\mycli.exe", AiCustomCliCommandError.RelativeExecutable)]
    [InlineData("~/bin/mycli", AiCustomCliCommandError.RelativeExecutable)]
    [InlineData("..", AiCustomCliCommandError.RelativeExecutable)]
    [InlineData("ORANDA_RUN_TOKEN=x mycli", AiCustomCliCommandError.ReservedVariable)]
    [InlineData("oranda_pipe=x mycli", AiCustomCliCommandError.ReservedVariable)]
    [InlineData("{prompt} --x", AiCustomCliCommandError.PlaceholderMisplaced)]
    [InlineData("KEY={prompt} mycli", AiCustomCliCommandError.PlaceholderMisplaced)]
    public void Invalid_commands_are_rejected_with_the_reason(string? command, AiCustomCliCommandError error) =>
        Assert.Equal(error, ErrorOf(command));

    [Fact]
    public void Absolute_paths_follow_the_rules_of_the_os_that_runs_the_command()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), AiCustomCliCommand.Parse("/usr/local/bin/mycli --x").Succeeded);
        foreach (var windowsPath in new[] { @"C:\tools\mycli.exe --x", "C:/tools/mycli.exe --x", @"\\server\share\mycli.exe" })
        {
            // 유닉스에서 이런 이름은 현재 폴더 아래의 상대 경로이므로 거부한다(다른 OS 에서 만든 설정을 그대로 실행하지 않는다).
            Assert.Equal(OperatingSystem.IsWindows(), AiCustomCliCommand.Parse(windowsPath).Succeeded);
        }

        // 드라이브 없는 루트(\x, /x)와 드라이브 상대 이름(C:x)은 Windows 에서 상대 경로다.
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf(@"\tools\mycli.exe"));
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf("/tools/mycli.exe"));
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf("C:mycli.exe"));
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf("C:"));
        }
        else
        {
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf("C:/evil"));
            Assert.Equal(AiCustomCliCommandError.RelativeExecutable, ErrorOf(@"\\x/evil"));
        }
    }

    [Theory]
    [InlineData("FOO\"=bar\" mycli")]
    [InlineData("ORANDA\"_\"X=1 mycli")]
    [InlineData("'ORANDA_X'=1 mycli")]
    public void A_quoted_name_never_becomes_an_environment_variable_and_never_hides_a_reserved_one(string command)
    {
        var result = AiCustomCliCommand.Parse(command);

        Assert.Empty(result.Command?.Environment ?? []);
    }

    [Fact]
    public void An_empty_assignment_value_and_empty_executable_forms_are_handled()
    {
        Assert.Equal([new KeyValuePair<string, string>("FOO", "")], Ok("FOO= mycli").Environment);
        Assert.Equal([new KeyValuePair<string, string>("FOO", "")], Ok("FOO=\"\" mycli").Environment);
        Assert.Equal(AiCustomCliCommandError.Empty, ErrorOf("\"\""));
        Assert.Equal(AiCustomCliCommandError.Empty, ErrorOf("FOO=bar \"\""));
    }

    [Fact]
    public void Only_a_space_or_a_tab_separates_words_and_other_blanks_outside_quotes_are_refused()
    {
        Assert.Equal(["--x"], Ok("mycli\t--x").Arguments);
        Assert.Equal(AiCustomCliCommandError.AmbiguousWhitespace, ErrorOf("mycli\u3000--x")); // 한글 입력기의 전각 공백
        Assert.Equal(AiCustomCliCommandError.AmbiguousWhitespace, ErrorOf("mycli\u00A0--x"));
        Assert.Equal(AiCustomCliCommandError.AmbiguousWhitespace, ErrorOf("/Users/x/Tool\u3000Dir/cli"));
    }

    [Fact]
    public void Those_blanks_are_kept_as_they_are_inside_quotes()
    {
        var command = Ok("mycli \"a\u3000b\" 'c\u00A0d'");

        Assert.Equal(["a\u3000b", "c\u00A0d"], command.Arguments);
    }

    [Fact]
    public void A_very_long_command_is_refused_before_it_is_split()
    {
        Assert.Equal(AiCustomCliCommandError.TooLong, ErrorOf("mycli " + new string('a', AiCustomCliCommand.MaxCommandLength)));
        Assert.True(AiCustomCliCommand.Parse("mycli " + new string('a', AiCustomCliCommand.MaxCommandLength - 6)).Succeeded);

        // 대입이 아주 많아도 빨리 끝난다(같은 이름 덮어쓰기에 제곱 시간이 들지 않는다).
        var many = string.Join(' ', Enumerable.Range(0, 800).Select(i => $"V{i % 50}={i}"));
        Assert.Equal(50, Ok(many + " mycli").Environment.Count);
    }

    [Theory]
    [InlineData("mycli 2>&1")]
    [InlineData("mycli &")]
    public void Redirects_and_background_markers_are_rejected(string command) =>
        Assert.Equal(AiCustomCliCommandError.ShellSyntax, ErrorOf(command));

    [Fact]
    public void A_double_quote_right_after_a_trailing_backslash_does_not_close_the_quote()
    {
        // 큰따옴표 안의 \" 는 따옴표 문자다. 경로 끝에 \ 를 두고 따옴표를 닫으려는 입력은 닫히지 않은 것으로 보고 알린다.
        Assert.Equal(AiCustomCliCommandError.UnterminatedQuote, ErrorOf(@"mycli --dir ""C:\work\"" -p"));
        Assert.Equal(@"C:\work", Ok(@"mycli --dir ""C:\work"" -p").Arguments[1]);
    }

    [Fact]
    public void Leading_assignments_are_environment_variables_and_the_last_value_wins()
    {
        var command = Ok("""FOO=1 BAR="two words" FOO=3 mycli X=not-env""");

        Assert.Equal("mycli", command.Executable);
        Assert.Equal(["X=not-env"], command.Arguments);
        Assert.Equal([new("BAR", "two words"), new("FOO", "3")], command.Environment);
    }

    [Fact]
    public void A_quoted_name_is_not_an_assignment()
    {
        var command = Ok("\"FOO=1\" --x");

        Assert.Equal("FOO=1", command.Executable);
        Assert.Empty(command.Environment);
    }

    [Fact]
    public void The_environment_keeps_the_last_value_and_compares_names_by_the_os_rule()
    {
        var command = Ok("Path=a PATH=b mycli");

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, command.Environment.Count);
        Assert.Equal("b", command.Environment[^1].Value);
    }

    [Fact]
    public void Placeholders_select_how_the_prompt_is_delivered_and_stdin_is_the_default()
    {
        Assert.True(Ok("mycli -p {prompt}").UsesPromptArgument);
        Assert.True(Ok("mycli --attach={prompt_file}").UsesPromptFile);
        var both = Ok("mycli {prompt} {prompt_file}");
        Assert.True(both.UsesPromptArgument && both.UsesPromptFile);
        Assert.False(both.SendsPromptOnStandardInput);
        Assert.True(Ok("mycli -p").SendsPromptOnStandardInput);
    }

    [Fact]
    public void Expansion_fills_each_placeholder_once_and_does_not_rescan_the_prompt()
    {
        var command = Ok("mycli -p {prompt} --attach={prompt_file}");

        Assert.True(command.TryExpand("글 {prompt_file} 안의 자리표시", "/tmp/p.txt", out var args, out var failure));
        Assert.Null(failure);
        Assert.Equal(["-p", "글 {prompt_file} 안의 자리표시", "--attach=/tmp/p.txt"], args);
    }

    [Fact]
    public void Expansion_fails_for_an_overlong_prompt_argument_or_a_missing_prompt_file()
    {
        var argument = Ok("mycli {prompt}");
        var overhead = OperatingSystem.IsWindows() ? 2 : 0; // Windows 는 인자마다 감싸는 따옴표 2자가 든다
        var room = AiCustomCliCommand.PromptArgumentLimit - overhead;
        Assert.False(argument.TryExpand(new string('a', room + 1), null, out _, out var tooLong));
        Assert.Equal("prompt_too_long", tooLong);
        Assert.True(argument.TryExpand(new string('a', room), null, out _, out _));

        // 따옴표는 Windows 에서 \" 로 늘어나므로 두 배로 센다(유닉스는 바이트 수라 한 바이트).
        Assert.Equal(!OperatingSystem.IsWindows(), argument.TryExpand(new string('"', room / 2 + 1), null, out _, out _));
        Assert.True(argument.TryExpand(new string('"', room / 2 - 1), null, out _, out _));

        // 한 글자가 3바이트인 글은 유닉스에서 바이트 수로 잰다(Windows 는 글자 수).
        var korean = new string('가', AiCustomCliCommand.PromptArgumentLimit / 2);
        Assert.Equal(OperatingSystem.IsWindows(), argument.TryExpand(korean, null, out _, out _));

        // {prompt} 를 여러 번 쓰면 치환된 전체 크기로 잰다.
        var twice = Ok("mycli {prompt} --again={prompt}");
        Assert.False(twice.TryExpand(new string('a', AiCustomCliCommand.PromptArgumentLimit / 2 + 10), null, out _, out var twiceFailure));
        Assert.Equal("prompt_too_long", twiceFailure);

        // 명령줄에 NUL 을 실을 수 없다.
        Assert.False(argument.TryExpand("a\0b", null, out _, out var invalid));
        Assert.Equal("prompt_invalid", invalid);
        Assert.True(Ok("mycli").TryExpand("a\0b", null, out _, out _));

        var file = Ok("mycli {prompt_file}");
        Assert.False(file.TryExpand("x", null, out _, out var missing));
        Assert.Equal("prompt_file_missing", missing);

        // 표준 입력 방식은 글 길이에 상한이 없다.
        Assert.True(Ok("mycli").TryExpand(new string('a', AiCustomCliCommand.PromptArgumentLimit * 3), null, out _, out _));
    }

    [Fact]
    public void Known_auto_approve_options_are_reported_but_never_block_parsing()
    {
        var command = Ok("mycli --yolo --approval-mode=auto_edit --permission-mode bypassPermissions --mode plan --approval-mode default --yolo");

        Assert.Equal(["--yolo", "--approval-mode auto_edit", "--permission-mode bypassPermissions"], command.AutoApproveOptions);
        Assert.Equal(["--yolo=true", "-a never", "-s danger-full-access"], Ok("mycli --yolo=true -a never -s danger-full-access -s read-only").AutoApproveOptions);
        Assert.Empty(Ok("mycli --model fast").AutoApproveOptions);
    }

    [Fact]
    public void Secret_looking_variables_with_a_value_are_reported_by_name_only()
    {
        var command = Ok("MY_API_KEY=sk-test TOKEN= PLAIN=1 DB_PASSWORD=x mycli");

        Assert.Equal(["MY_API_KEY", "DB_PASSWORD"], command.SecretEnvironmentNames);
    }

    [Fact]
    public void Secret_detection_leans_towards_reporting_more()
    {
        var command = Ok("DB_PASS=1 ORACLE_PWD=2 ACCESS_KEY=3 OPENAI_KEY=4 DATABASE_URL=5 AWS_ACCESS_KEY_ID=6 NODE_ENV=prod KEYBOARD=us mycli");

        Assert.Equal(["DB_PASS", "ORACLE_PWD", "ACCESS_KEY", "OPENAI_KEY", "DATABASE_URL", "AWS_ACCESS_KEY_ID"], command.SecretEnvironmentNames);
    }

    [Fact]
    public void Loader_and_runtime_variables_are_reported_but_not_refused()
    {
        var command = Ok("LD_PRELOAD=/x.so DYLD_INSERT_LIBRARIES=/y NODE_OPTIONS=--inspect PYTHONPATH=/z PATH=/bin HOME=/h mycli");

        Assert.Equal(["LD_PRELOAD", "DYLD_INSERT_LIBRARIES", "NODE_OPTIONS", "PYTHONPATH", "PATH"], command.RiskyEnvironmentNames);
        Assert.Empty(Ok("A=1 mycli").RiskyEnvironmentNames);
    }

    [Fact]
    public void Every_error_has_a_fixed_message()
    {
        foreach (var error in Enum.GetValues<AiCustomCliCommandError>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AiCustomCliCommand.MessageOf(error)));
        }
    }
}
