using System.Text;
using System.Text.RegularExpressions;

namespace Oranda.Core.Ai.Profiles;

/// <summary>기타 CLI 명령 문자열을 분해하지 못한 이유. 문구는 <see cref="AiCustomCliCommand.MessageOf"/> 가 고정된 글로 낸다(번역 표에 넣을 수 있게 값을 끼워 넣지 않는다).</summary>
public enum AiCustomCliCommandError
{
    /// <summary>명령이 비었거나 실행 파일 자리가 없다(환경 변수만 있다).</summary>
    Empty,

    /// <summary>따옴표가 닫히지 않았다.</summary>
    UnterminatedQuote,

    /// <summary>셸 기호(파이프·세미콜론·앰퍼샌드·리디렉션·백틱·<c>$(</c>)가 있다. 명령은 셸 없이 실행하므로 해석되지 않는다.</summary>
    ShellSyntax,

    /// <summary>줄바꿈 등 제어 문자가 있다(한 줄 명령만 받는다).</summary>
    ControlCharacter,

    /// <summary>따옴표 밖에 전각 공백·줄 바꿈 없는 공백(NBSP) 같은 보이지 않는 공백이 있다. 낱말이 조용히 나뉘어 다른 파일을 실행하는 일을 막으려고 거부한다.</summary>
    AmbiguousWhitespace,

    /// <summary>명령이 <see cref="AiCustomCliCommand.MaxCommandLength"/> 를 넘는다.</summary>
    TooLong,

    /// <summary>폴더 구분자가 든 상대 경로 실행 파일이다. 이름만 쓰거나 절대 경로를 써야 한다.</summary>
    RelativeExecutable,

    /// <summary><c>ORANDA_</c> 로 시작하는 환경 변수다(앱이 쓰는 이름 공간이라 쓸 수 없다).</summary>
    ReservedVariable,

    /// <summary><c>{prompt}</c>·<c>{prompt_file}</c> 가 실행 파일이나 환경 변수 값에 있다(인자에만 쓸 수 있다).</summary>
    PlaceholderMisplaced,
}

/// <summary>
/// <see cref="AiCustomCliCommand.Parse"/> 의 결과. 성공이면 <see cref="Command"/>, 실패면 <see cref="Error"/> 가 있다.
/// </summary>
public sealed record AiCustomCliParseResult(AiCustomCliCommand? Command, AiCustomCliCommandError? Error)
{
    public bool Succeeded => Command is not null;
}

/// <summary>
/// 사용자가 등록한 기타 CLI 의 명령 문자열을 셸 없이 인자로 분해한 결과.
/// <list type="bullet">
/// <item>따옴표: 작은따옴표 안은 그대로, 큰따옴표 안은 <c>\"</c> 만 따옴표 하나로 바꾼다(역슬래시는 Windows 경로에 쓰이므로 그 밖에는 그대로 둔다).</item>
/// <item>앞쪽의 <c>NAME=value</c> 는 자식 프로세스의 환경 변수이고, 그 뒤 첫 낱말이 실행 파일이다.</item>
/// <item><c>{prompt_file}</c>(0600 임시 파일 경로)와 <c>{prompt}</c>(글 자체, 명령줄에 노출됨)는 인자 안에서만 쓸 수 있고, 둘 다 없으면 글은 표준 입력으로 보낸다.</item>
/// <item>거부: 따옴표 밖 셸 기호, 큰따옴표 안의 백틱·<c>$(</c>, 폴더 구분자가 든 상대 경로 실행 파일, <c>ORANDA_</c> 변수, 줄바꿈.</item>
/// </list>
/// 이 분해는 실행 파일 존재·배치 파일 여부를 보지 않는다(그건 실행하는 쪽의 몫). 자동 승인 옵션과 비밀로 보이는 변수는 막지 않고 표시용으로만 알린다.
/// </summary>
public sealed record AiCustomCliCommand
{
    /// <summary><c>{prompt}</c> 를 쓸 때 치환된 인자 전체가 명령줄에서 차지하는 크기의 상한(OS 상한 아래에서 보수적으로 잡은 값. Windows 는 글자 수, 그 밖은 UTF-8 바이트 수).</summary>
    public static int PromptArgumentLimit => OperatingSystem.IsWindows() ? WindowsPromptChars : UnixPromptBytes;

    /// <summary>명령 문자열 길이 상한(잘못 붙여넣은 긴 글이 화면을 멈추지 않게).</summary>
    public const int MaxCommandLength = 8192;

    private const int WindowsPromptChars = 24_000;
    private const int UnixPromptBytes = 100_000;

    private static readonly Regex Placeholders = new(@"\{prompt_file\}|\{prompt\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AssignmentName = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SecretName = new(@"API[_-]?KEY|ACCESS[_-]?KEY|PRIVATE[_-]?KEY|(?:^|[_-])KEY$|TOKEN|SECRET|PASS|PWD|CREDENTIAL|AUTH|DATABASE[_-]?URL|CONNECTION[_-]?STRING|DSN", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 로더·런타임 동작을 바꾸는 환경 변수(표시용 경고 대상. 등록하는 사람이 사용자 자신이므로 막지 않는다).
    private static readonly HashSet<string> RiskyEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH", "DYLD_FALLBACK_LIBRARY_PATH",
        "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME", "PERL5OPT", "PERL5LIB", "RUBYOPT", "RUBYLIB",
        "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH", "BASH_ENV", "ENV", "PATH",
    };

    // 값이 없는 자동 승인 옵션.
    private static readonly HashSet<string> AutoApproveFlags = new(StringComparer.Ordinal)
    {
        "--yolo", "-y", "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--dangerously-bypass-approvals-and-sandbox",
        "--full-auto", "--auto-approve", "--auto-edit", "--skip-permissions", "--trust-all-tools", "--allow-all-tools", "--approve-all",
    };

    // 값에 따라 자동 승인이 되는 옵션(옵션 → 위험한 값들).
    private static readonly Dictionary<string, string[]> AutoApproveValues = new(StringComparer.Ordinal)
    {
        ["--approval-mode"] = ["yolo", "auto_edit"],
        ["--mode"] = ["yolo"],
        ["--permission-mode"] = ["bypassPermissions", "acceptEdits"],
        ["--ask-for-approval"] = ["never"],
        ["--approval-policy"] = ["never"],
        ["--sandbox"] = ["danger-full-access"],
        ["-a"] = ["never"],
        ["-s"] = ["danger-full-access"],
    };

    /// <summary>실행 파일: 이름만(PATH 에서 찾음) 또는 절대 경로.</summary>
    public required string Executable { get; init; }

    /// <summary>인자 틀(자리표시가 그대로 있다). 글을 넣어 실제 인자로 만드는 일은 <see cref="TryExpand"/>.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>자식 프로세스에 더할 환경 변수(같은 이름이 여러 번이면 마지막 값). 값이 평문으로 설정에 남는다.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Environment { get; init; }

    /// <summary><c>{prompt}</c> 를 쓴다(글이 명령줄에 실려 다른 프로세스에도 보인다). 인자에서 계산하므로 인자와 어긋나지 않는다.</summary>
    public bool UsesPromptArgument => Arguments.Any(a => a.Contains("{prompt}", StringComparison.Ordinal));

    /// <summary><c>{prompt_file}</c> 를 쓴다(글을 0600 임시 파일에 두고 그 경로를 넘긴다). 인자에서 계산한다.</summary>
    public bool UsesPromptFile => Arguments.Any(a => a.Contains("{prompt_file}", StringComparison.Ordinal));

    /// <summary>자리표시가 없어 글을 표준 입력으로 보낸다.</summary>
    public bool SendsPromptOnStandardInput => !UsesPromptArgument && !UsesPromptFile;

    /// <summary>알려진 자동 승인 옵션이 든 인자(표시용, 예: <c>--approval-mode yolo</c>). 저장을 막지 않는다.</summary>
    public IReadOnlyList<string> AutoApproveOptions { get; init; } = [];

    /// <summary>값이 있는데 이름이 비밀로 보이는 환경 변수 이름(표시용). 값은 설정에 평문으로 남는다. 오탐보다 누락을 줄이는 쪽으로 넓게 잡는다.</summary>
    public IReadOnlyList<string> SecretEnvironmentNames { get; init; } = [];

    /// <summary>프로그램 로딩·런타임 동작을 바꾸는 환경 변수 이름(<c>LD_PRELOAD</c>·<c>NODE_OPTIONS</c>·<c>PATH</c> 등, 표시용). 막지 않는다.</summary>
    public IReadOnlyList<string> RiskyEnvironmentNames { get; init; } = [];

    /// <summary>실패 이유를 고정된 한국어 글로 바꾼다(값을 끼워 넣지 않는다).</summary>
    public static string MessageOf(AiCustomCliCommandError error) => error switch
    {
        AiCustomCliCommandError.Empty => "실행 명령이 비어 있습니다. 실행 파일 이름이나 절대 경로를 입력하세요.",
        AiCustomCliCommandError.UnterminatedQuote => "따옴표가 닫히지 않았습니다. 큰따옴표 안의 \\\" 는 따옴표 문자로 읽으므로, 경로 끝의 \\ 바로 뒤에 닫는 따옴표를 붙이지 마세요.",
        AiCustomCliCommandError.ShellSyntax => "셸 기호(| ; & < > ` $()는 쓸 수 없습니다. 명령은 셸 없이 실행하므로 해석되지 않습니다. 글자 그대로 쓰려면 작은따옴표 안에 넣으세요(큰따옴표 안에서는 ` 와 $( 를 쓸 수 없습니다).",
        AiCustomCliCommandError.ControlCharacter => "명령은 한 줄로 입력하세요.",
        AiCustomCliCommandError.AmbiguousWhitespace => "따옴표 밖에 전각 공백 같은 보이지 않는 공백이 있습니다. 낱말은 일반 공백이나 탭으로 나누세요. 경로에 그 공백이 들어 있다면 따옴표로 감싸세요.",
        AiCustomCliCommandError.TooLong => "실행 명령이 너무 깁니다(8192자 이하).",
        AiCustomCliCommandError.RelativeExecutable => "폴더 구분자가 든 상대 경로는 쓸 수 없습니다. 실행 파일 이름만 쓰거나 절대 경로를 입력하세요.",
        AiCustomCliCommandError.ReservedVariable => "ORANDA_ 로 시작하는 환경 변수는 쓸 수 없습니다.",
        AiCustomCliCommandError.PlaceholderMisplaced => "{prompt}·{prompt_file} 는 실행 파일이나 환경 변수 값이 아닌 인자에만 쓸 수 있습니다.",
        _ => "실행 명령을 읽을 수 없습니다.",
    };

    /// <summary>명령 문자열을 분해한다. 실패해도 예외를 던지지 않고 이유를 낸다.</summary>
    public static AiCustomCliParseResult Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail(AiCustomCliCommandError.Empty);
        }

        if (command.Length > MaxCommandLength)
        {
            return Fail(AiCustomCliCommandError.TooLong);
        }

        if (!Tokenize(command, out var tokens, out var error))
        {
            return Fail(error);
        }

        // 같은 이름은 마지막 값이 이긴다. Windows 의 환경 변수 이름은 대소문자를 구분하지 않는다(Path 와 PATH 는 같은 변수).
        var assignments = new Dictionary<string, (int Order, string Name, string Value)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var index = 0;
        while (index < tokens.Count && IsAssignment(tokens[index], out var name, out var value))
        {
            if (name.StartsWith("ORANDA_", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(AiCustomCliCommandError.ReservedVariable);
            }

            if (Placeholders.IsMatch(value))
            {
                return Fail(AiCustomCliCommandError.PlaceholderMisplaced);
            }

            assignments[name] = (index, name, value);
            index++;
        }

        var environment = assignments.Values.OrderBy(a => a.Order).Select(a => new KeyValuePair<string, string>(a.Name, a.Value)).ToList();

        if (index >= tokens.Count || tokens[index].Text.Length == 0)
        {
            return Fail(AiCustomCliCommandError.Empty);
        }

        var executable = tokens[index].Text;
        if (Placeholders.IsMatch(executable))
        {
            return Fail(AiCustomCliCommandError.PlaceholderMisplaced);
        }

        if (IsRelativeWithSeparator(executable) || executable is "." or "..")
        {
            return Fail(AiCustomCliCommandError.RelativeExecutable);
        }

        var arguments = tokens.Skip(index + 1).Select(t => t.Text).ToList();
        var parsed = new AiCustomCliCommand
        {
            Executable = executable,
            Arguments = arguments,
            Environment = environment,
            AutoApproveOptions = FindAutoApprove(arguments),
            SecretEnvironmentNames = [.. environment.Where(p => p.Value.Length > 0 && SecretName.IsMatch(p.Key)).Select(p => p.Key)],
            RiskyEnvironmentNames = [.. environment.Where(p => RiskyEnvironment.Contains(p.Key)).Select(p => p.Key)],
        };
        return new AiCustomCliParseResult(parsed, null);
    }

    /// <summary>
    /// 자리표시를 채운 실제 인자를 만든다. 자리표시는 한 번에 치환하므로 글 안에 <c>{prompt_file}</c> 같은 문자열이 있어도 다시 치환되지 않는다.
    /// <c>{prompt}</c> 를 쓰는데 글이 <see cref="PromptArgumentLimit"/> 를 넘거나, <c>{prompt_file}</c> 를 쓰는데 경로가 없으면 실패한다.
    /// </summary>
    /// <param name="prompt">보낼 글.</param>
    /// <param name="promptFilePath">글을 담은 임시 파일 경로. <see cref="UsesPromptFile"/> 이 아니면 null 이어도 된다.</param>
    /// <param name="arguments">성공이면 실제 인자 목록.</param>
    /// <param name="failure">실패 이유: <c>prompt_too_long</c> 또는 <c>prompt_file_missing</c>.</param>
    public bool TryExpand(string prompt, string? promptFilePath, out IReadOnlyList<string> arguments, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        arguments = [];
        failure = null;
        if (UsesPromptFile && string.IsNullOrEmpty(promptFilePath))
        {
            failure = "prompt_file_missing";
            return false;
        }

        // NUL 은 유닉스의 인자를 그 자리에서 자르고 Windows 의 프로세스 시작을 실패시킨다.
        if (UsesPromptArgument && prompt.Contains('\0'))
        {
            failure = "prompt_invalid";
            return false;
        }

        var expanded = Arguments.Select(a => Placeholders.Replace(a, m => m.Value == "{prompt}" ? prompt : promptFilePath!)).ToList();

        // {prompt} 를 여러 번 쓰면 그만큼 늘어나므로, 글 하나가 아니라 치환된 뒤 인자 전체의 크기로 잰다.
        if (UsesPromptArgument && ArgumentSize(expanded) > PromptArgumentLimit)
        {
            failure = "prompt_too_long";
            return false;
        }

        arguments = expanded;
        return true;
    }

    // 명령줄에 실리는 크기: Windows 는 글자 수(인자마다 감싸는 따옴표 2자를 더하고, 글 안의 따옴표·역슬래시는 이스케이프로 늘어나므로 두 배로 센다), 그 밖은 UTF-8 바이트 수. 인자 사이 구분 공백을 더한다.
    private static long ArgumentSize(List<string> arguments)
    {
        long size = Math.Max(0, arguments.Count - 1);
        foreach (var argument in arguments)
        {
            size += OperatingSystem.IsWindows()
                ? argument.Length + 2 + argument.Count(c => c is '"' or '\\')
                : Encoding.UTF8.GetByteCount(argument);
        }

        return size;
    }

    private static AiCustomCliParseResult Fail(AiCustomCliCommandError error) => new(null, error);

    // 구분자(/ 와 \)가 든 이름이나 드라이브 상대 이름(Windows 의 C:x)은 절대 경로일 때만 받는다.
    // 절대 경로의 기준은 실행하는 OS 의 것이다: 유닉스는 / 로 시작할 때만, Windows 는 드라이브+구분자(C:\)나 UNC(\\서버\공유)일 때만 절대다.
    // 다른 OS 에서 만든 설정의 경로는 여기서 상대 경로로 거부되어 사용자가 고치게 된다(현재 폴더 아래의 파일을 실행하는 일을 막는다).
    private static bool IsRelativeWithSeparator(string path)
    {
        var hasSeparator = path.Contains('/') || path.Contains('\\');
        var driveRelative = OperatingSystem.IsWindows() && path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        return (hasSeparator || driveRelative) && !IsAbsolute(path);
    }

    private static bool IsAbsolute(string path) =>
        OperatingSystem.IsWindows()
            ? path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
                || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            : path.StartsWith('/');

    private static bool IsAssignment(Token token, out string name, out string value)
    {
        name = value = "";
        var match = AssignmentName.Match(token.Text);
        if (!match.Success || (token.FirstQuoteAt >= 0 && token.FirstQuoteAt < match.Length))
        {
            return false;
        }

        name = token.Text[..(match.Length - 1)];
        value = token.Text[match.Length..];
        return true;
    }

    private static List<string> FindAutoApprove(List<string> arguments)
    {
        var found = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            var (option, inlineValue) = eq > 0 ? (arg[..eq], arg[(eq + 1)..]) : (arg, null);
            if (AutoApproveFlags.Contains(option))
            {
                found.Add(arg);
                continue;
            }

            if (!AutoApproveValues.TryGetValue(option, out var risky))
            {
                continue;
            }

            var value = inlineValue ?? (i + 1 < arguments.Count ? arguments[i + 1] : null);
            if (value is not null && risky.Contains(value, StringComparer.Ordinal))
            {
                found.Add($"{option} {value}");
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    private readonly record struct Token(string Text, int FirstQuoteAt);

    private static bool Tokenize(string command, out List<Token> tokens, out AiCustomCliCommandError error)
    {
        var result = new List<Token>();
        tokens = result;
        error = default;
        var current = new StringBuilder();
        var inToken = false;
        var firstQuote = -1;
        var quote = '\0';

        void EndToken()
        {
            if (inToken)
            {
                result.Add(new Token(current.ToString(), firstQuote));
            }

            current.Clear();
            inToken = false;
            firstQuote = -1;
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            var next = i + 1 < command.Length ? command[i + 1] : '\0';
            if (char.IsControl(c) && c != '\t')
            {
                error = AiCustomCliCommandError.ControlCharacter;
                return false;
            }

            switch (quote)
            {
                case '\'':
                    if (c == '\'')
                    {
                        quote = '\0';
                    }
                    else
                    {
                        current.Append(c);
                    }

                    break;
                case '"':
                    if (c == '"')
                    {
                        quote = '\0';
                    }
                    else if (c == '`' || (c == '$' && next == '('))
                    {
                        error = AiCustomCliCommandError.ShellSyntax;
                        return false;
                    }
                    else if (c == '\\' && next == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        current.Append(c);
                    }

                    break;
                default:
                    if (c is ' ' or '\t')
                    {
                        EndToken();
                    }
                    else if (char.IsWhiteSpace(c))
                    {
                        // 전각 공백·NBSP 같은 보이지 않는 공백이 따옴표 밖에 있으면 경로가 조용히 둘로 나뉘어 다른 파일을 실행할 수 있다.
                        error = AiCustomCliCommandError.AmbiguousWhitespace;
                        return false;
                    }
                    else if (c is '\'' or '"')
                    {
                        quote = c;
                        firstQuote = firstQuote < 0 ? current.Length : firstQuote;
                        inToken = true;
                    }
                    else if (c is '|' or ';' or '&' or '<' or '>' or '`' || (c == '$' && next == '('))
                    {
                        error = AiCustomCliCommandError.ShellSyntax;
                        return false;
                    }
                    else
                    {
                        current.Append(c);
                        inToken = true;
                    }

                    break;
            }
        }

        if (quote != '\0')
        {
            error = AiCustomCliCommandError.UnterminatedQuote;
            return false;
        }

        EndToken();
        return true;
    }
}
