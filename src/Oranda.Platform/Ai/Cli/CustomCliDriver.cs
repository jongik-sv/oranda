using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
using Oranda.Core.Settings;
using Oranda.Platform.Ai.Api;
using Oranda.Platform.Ai.Profiles;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 사용자가 등록한 기타 CLI 를 한 번 실행해 글만 주고받는 드라이버. DB 도구·실행 토큰·파이프를 쓰지 않고(<c>SystemCliRunner</c> 를 쓰지 않는다),
/// 라우터가 가림 대상 요청을 걸러 낸 뒤에만 부른다. 이미지는 지원하지 않는다.
/// <list type="bullet">
/// <item>명령 문자열은 <see cref="AiCustomCliCommand"/> 가 셸 없이 분해한다. 글은 표준 입력(기본)·<c>{prompt_file}</c>(임시 파일)·<c>{prompt}</c>(명령줄) 중 명령이 정한 쪽으로 보낸다.</item>
/// <item>실행 파일은 <see cref="CustomCliLocator"/> 가 전체 경로로 풀어서(상대 PATH 항목은 건너뜀) 그 경로로 시작한다. 명령에 <c>PATH=</c> 가 있으면 찾기와 실행 모두 그 값을 쓰고, 실행 파일 폴더를 PATH 앞에 둔다.</item>
/// <item>프롬프트에는 호출한 쪽이 보낸 글(<see cref="AiRequest.Prompt"/>)과 앞 대화만 넣는다. 접속 이름·환경·대상 문장·sql_id 같은 <see cref="AiRequest.Context"/> 와 <see cref="AiRequest.Session"/> 은 읽지 않는다(라우터 판단에만 쓰인다).</item>
/// <item>자식 환경에서 <c>ORANDA_</c> 변수(실행 토큰 등)를 지운다. 이 CLI 가 낸 오류 글·발췌에서는 비밀로 보이는 이름의 환경 변수 값을 가린다.</item>
/// <item>작업 폴더는 앱 데이터 밖(시스템 임시 폴더)의 빈 폴더이고 끝나면 지운다. 프로젝트 파일을 우연히 읽지 않게 할 뿐 격리(샌드박스)가 아니다: CLI 는 사용자 권한으로 무엇이든 할 수 있다.</item>
/// <item>이어 말하기는 메모리 대화록과 합성 ResumeId 로 한다(CLI 의 자체 세션 기능은 쓰지 않는다). 매 호출에 앞 대화를 글로 붙인다. 명령이 바뀌면 다른 대화다. 실패·취소한 실행은 대화에 남지 않는다.</item>
/// <item>구조 결과는 스키마를 프롬프트에 붙이고 출력에서 JSON 을 찾아 검증한다. 찾지 못하면 <see cref="StructuredInvalidCode"/> 오류로 이유와 CLI 의 글을 알린다.</item>
/// <item>출력은 ANSI 를 지우고 크기를 제한한다(넘치면 앞부분을 버리고 뒤를 남긴다). 0 이 아닌 종료 코드는 오류, 실행 제한은 30분이며 취소·시간 초과에 프로세스 트리를 끝낸다.
/// CLI 가 끝난 뒤에도 손자 프로세스가 출력 파이프를 쥐고 있으면 짧은 유예 뒤 읽기를 끊고 받은 만큼으로 답한다(그 손자 프로세스는 이 드라이버가 끝내지 못한다).</item>
/// </list>
/// </summary>
[OrandaService(typeof(ICustomCliAiDriver))]
internal sealed class CustomCliDriver : ICustomCliAiDriver
{
    /// <summary>실행 한 번의 시간 제한.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>CLI 프로세스가 끝난 뒤 출력 파이프가 닫히기를 기다리는 시간.</summary>
    internal static readonly TimeSpan DefaultExitGrace = TimeSpan.FromSeconds(2);

    /// <summary>구조 결과를 찾지 못했을 때의 오류 코드(API 경로와 같다).</summary>
    public const string StructuredInvalidCode = OpenAiApiDriver.StructuredInvalidCode;

    private const int MaxOutputChars = 2_000_000;
    private const int MaxErrorChars = 1000;
    private const int MaxExcerptChars = 800;
    private const string WorkspacePrefix = "oranda-custom-cli-";
    private const string TruncatedNotice = "(출력이 너무 길어 앞부분을 생략했습니다.)";

    private const string SystemPrompt =
        "당신은 Oracle 데이터베이스 SQL 튜닝을 돕는 도우미입니다. 이 대화에서는 도구를 쓰지 말고, 아래에 주어진 글만 근거로 답하세요. "
        + "주어지지 않은 사실은 추측하지 말고 알 수 없다고 밝히세요.";

    private readonly IClaudeProcessLauncher _launcher;
    private readonly CustomCliLocator _locator;
    private readonly Func<IEnumerable<string>> _environmentNames;
    private readonly string _tempRoot;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _exitGrace;
    private readonly OpenAiConversationStore _conversations = new(new OpenAiApiOptions());
    private int _swept;

    public CustomCliDriver()
        : this(
            new SystemClaudeProcessLauncher(),
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            () => Environment.GetEnvironmentVariables().Keys.Cast<string>(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath(),
            DefaultTimeout)
    {
    }

    /// <summary>시험용: 프로세스 실행·경로 찾기·환경·임시 폴더·시간 제한을 바꿔 넣는다.</summary>
    internal CustomCliDriver(
        IClaudeProcessLauncher launcher,
        Func<string?> pathVariable,
        Func<string, bool> fileExists,
        Func<IEnumerable<string>> environmentNames,
        string home,
        string tempRoot,
        TimeSpan timeout,
        TimeSpan? exitGrace = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _locator = new CustomCliLocator(pathVariable, fileExists, home);
        _environmentNames = environmentNames ?? throw new ArgumentNullException(nameof(environmentNames));
        _tempRoot = tempRoot ?? throw new ArgumentNullException(nameof(tempRoot));
        _timeout = timeout;
        _exitGrace = exitGrace ?? DefaultExitGrace;
    }

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiCustomCli cli, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cli);

        if (Precheck(request, cli, out var command, out var executable) is { } rejected)
        {
            yield return rejected;
            yield break;
        }

        var owner = $"custom:{cli.Id}|{AiCustomCliTarget.CommandHash(cli.Command)}";
        var (lease, openError) = _conversations.Open(request.ResumeId, owner, SystemPrompt);
        if (openError is not null)
        {
            yield return openError;
            yield break;
        }

        using (lease!)
        {
            yield return new AiStarted(lease!.Id);

            var structured = request.OutputSchema is { ValueKind: JsonValueKind.Object };
            var prompt = ComposePrompt(lease.Messages, request.Prompt, structured ? request.OutputSchema : null);
            var (workspace, workspaceError) = PrepareWorkspace(prompt, command!.UsesPromptFile);
            if (workspaceError is not null)
            {
                yield return workspaceError;
                yield break;
            }

            using (workspace!)
            {
                if (!command.TryExpand(prompt, workspace!.PromptFile, out var arguments, out var failure))
                {
                    yield return new AiError(ExpandFailureMessage(failure), failure);
                    yield break;
                }

                var launch = new ClaudeLaunch(
                    executable!,
                    arguments,
                    workspace.Directory,
                    command.SendsPromptOnStandardInput ? prompt : string.Empty,
                    BuildEnvironment(command, executable!),
                    MaxLineChars: MaxOutputChars);
                var redact = SecretRedaction(command);

                var run = await RunProcessAsync(launch, ct).ConfigureAwait(false);
                if (run.StartError is not null)
                {
                    yield return new AiError($"{DisplayName(cli)} 을(를) 실행하지 못했습니다: {redact(run.StartError)}", "custom_cli_start_failed");
                    yield break;
                }

                if (run.TimedOut)
                {
                    yield return new AiError($"{DisplayName(cli)} 이(가) 제한 시간({(int)_timeout.TotalMinutes}분) 안에 끝나지 않아 중단했습니다.", "custom_cli_timeout");
                    yield break;
                }

                if (run.ExitCode != 0)
                {
                    yield return new AiError(redact(FailureMessage(cli, run)), "custom_cli_failed");
                    yield break;
                }

                var output = run.Output.Trim();
                if (output.Length == 0)
                {
                    yield return new AiError($"{DisplayName(cli)} 이(가) 아무 글도 내지 않았습니다.", "custom_cli_empty_output");
                    yield break;
                }

                if (structured)
                {
                    // CLI 가 입력을 그대로 되풀이해 내보내도 우리가 붙인 스키마·앞 대화의 JSON 을 결과로 집지 않게 먼저 뺀다.
                    var searchable = output.Replace(prompt, string.Empty, StringComparison.Ordinal);
                    var (result, found, issues) = CustomCliOutput.FindResult(searchable, request.OutputSchema!.Value);
                    if (result is null)
                    {
                        yield return new AiError(redact(StructuredFailureMessage(cli, found, issues, output, run.OutputTruncated)), StructuredInvalidCode);
                        yield break;
                    }

                    Commit(lease, request.Prompt, output);
                    yield return new AiFinal(null, result, lease.Id);
                    yield break;
                }

                var text = run.OutputTruncated ? TruncatedNotice + "\n" + output : output;
                Commit(lease, request.Prompt, text);
                yield return new AiFinal(redact(text), null, lease.Id);
            }
        }
    }

    private static string DisplayName(AiCustomCli cli) => cli.Name.Trim().Length > 0 ? $"「{cli.Name.Trim()}」" : "기타 CLI";

    private static string ExpandFailureMessage(string? failure) => failure switch
    {
        "prompt_too_long" => "대화 글이 너무 길어 명령줄({prompt})로 보낼 수 없습니다. 실행 명령에서 {prompt} 대신 표준 입력(자리표시 없음)이나 {prompt_file} 을 쓰세요.",
        "prompt_invalid" => "글에 명령줄로 보낼 수 없는 문자(NUL)가 있습니다. 실행 명령에서 {prompt} 대신 표준 입력(자리표시 없음)이나 {prompt_file} 을 쓰세요.",
        _ => "글을 담은 임시 파일의 경로를 만들지 못했습니다.",
    };

    private AiError? Precheck(AiRequest request, AiCustomCli cli, out AiCustomCliCommand? command, out string? executable)
    {
        command = null;
        executable = null;
        if (request.Images is { Count: > 0 })
        {
            return new AiError(RoutingAiDriver.CustomCliImagesUnsupportedMessage, RoutingAiDriver.CustomCliImagesUnsupportedCode);
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return new AiError("보낼 글이 비어 있습니다.", "empty_prompt");
        }

        var parsed = AiCustomCliCommand.Parse(cli.Command);
        if (parsed.Command is null)
        {
            return new AiError($"{DisplayName(cli)} 의 {AiCustomCliCommand.MessageOf(parsed.Error!.Value)}", "custom_cli_command_invalid");
        }

        command = parsed.Command;
        var (path, error) = _locator.Resolve(command.Executable, DisplayName(cli), CommandPath(command));
        executable = path;
        return error;
    }

    // 명령에 적은 PATH 변수의 값(Windows 는 이름의 대소문자를 구분하지 않는다). 없으면 null.
    private static string? CommandPath(AiCustomCliCommand command)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (name, value) in command.Environment)
        {
            if (string.Equals(name, "PATH", comparison))
            {
                return value;
            }
        }

        return null;
    }

    // 부모 환경의 ORANDA_ 변수(실행 토큰·파이프 이름 등)는 자식에게 넘기지 않는다. 명령에 적은 변수는 그 위에 얹는다(ORANDA_ 변수는 분해 단계에서 거부).
    // 명령이 PATH 를 정했으면 실행 파일 폴더를 그 앞에 둔다(시작 경로가 명령 변수를 마지막에 적용하므로 여기서 미리 합쳐야 폴더가 사라지지 않는다).
    private Dictionary<string, string?> BuildEnvironment(AiCustomCliCommand command, string executable)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in _environmentNames().Where(n => n.StartsWith("ORANDA_", StringComparison.OrdinalIgnoreCase)))
        {
            environment[name] = null;
        }

        foreach (var (name, value) in command.Environment)
        {
            environment[name] = value;
        }

        var directory = Path.GetDirectoryName(executable);
        var pathKey = environment.Keys.FirstOrDefault(k => string.Equals(k, "PATH", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (pathKey is not null && environment[pathKey] is { } path && !string.IsNullOrEmpty(directory)
            && !path.Split(Path.PathSeparator).Contains(directory, StringComparer.Ordinal))
        {
            environment[pathKey] = path.Length == 0 ? directory : directory + Path.PathSeparator + path;
        }

        return environment;
    }

    // 오류 글·답에서 가릴 값: 비밀로 보이는 이름의 환경 변수 값(명령에 평문으로 적혀 있다).
    // 이름 판정이 넓어(ORACLE_DSN·AUTH_ENABLED 도 걸린다) 값이 흔한 낱말이면 답의 모든 같은 낱말이 ***로 바뀌어 글이 망가진다.
    // 그래서 MinSecretLength 글자 미만, true/false 같은 낱말, 숫자만으로 된 값은 가리지 않는다. 대가: 그보다 짧은 진짜 비밀은 글에 그대로 보일 수 있다.
    private static Func<string, string> SecretRedaction(AiCustomCliCommand command)
    {
        var secrets = command.Environment.Where(p => command.SecretEnvironmentNames.Contains(p.Key)).Select(p => p.Value).Where(IsMaskableSecret).ToList();
        return text => secrets.Aggregate(text, (current, secret) => SecretRedactor.Redact(current, secret));
    }

    /// <summary>가릴 값의 최소 길이. 이보다 짧은 값(예: ORCL)은 일반 낱말과 구별할 수 없어 가리지 않는다.</summary>
    internal const int MinSecretLength = 5;

    private static readonly HashSet<string> PlainWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "none", "null", "enabled", "disabled", "debug", "info", "warn", "error", "default",
    };

    internal static bool IsMaskableSecret(string value) =>
        value.Length >= MinSecretLength && !PlainWords.Contains(value) && !value.All(char.IsDigit);

    private (Workspace? Workspace, AiError? Error) PrepareWorkspace(string prompt, bool needsFile)
    {
        Workspace? workspace = null;
        try
        {
            if (Interlocked.Exchange(ref _swept, 1) == 0)
            {
                Workspace.SweepStale(_tempRoot);
            }

            workspace = Workspace.Create(_tempRoot);
            if (needsFile)
            {
                workspace.WritePromptFile(prompt);
            }

            return (workspace, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            workspace?.Dispose();
            return (null, new AiError($"임시 작업 폴더를 만들지 못했습니다: {ex.Message}", "custom_cli_workspace_failed"));
        }
    }

    private async Task<ProcessResult> RunProcessAsync(ClaudeLaunch launch, CancellationToken ct)
    {
        IClaudeProcess process;
        try
        {
            process = _launcher.Start(launch);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ProcessResult(-1, string.Empty, false, string.Empty, false, ex.Message);
        }

        await using (process.ConfigureAwait(false))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_timeout);

            // 파이프 읽기가 취소를 늦게 반영해도 프로세스 트리는 바로 끝낸다.
            using var killOnCancel = limit.Token.Register(process.Kill);
            using var stopReading = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            var output = new OutputBuffer(MaxOutputChars);
            var reader = ReadAsync(process, output, stopReading.Token);
            try
            {
                var exit = await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

                // 프로세스가 끝났는데 손자 프로세스가 출력 파이프를 쥐고 있으면 EOF 가 오지 않는다. 짧게 기다린 뒤 받은 만큼으로 끝낸다.
                if (await Task.WhenAny(reader, Task.Delay(_exitGrace, limit.Token)).ConfigureAwait(false) != reader)
                {
                    await stopReading.CancelAsync().ConfigureAwait(false);
                }

                await reader.ConfigureAwait(false);

                // 취소·시간 초과로 읽기가 먼저 멈췄다면 정상 종료로 보지 않는다.
                limit.Token.ThrowIfCancellationRequested();
                var text = output.ToString();
                return new ProcessResult(exit, text, output.Truncated || process.OutputLineTruncated, CustomCliOutput.StripAnsi(process.StandardErrorTail), false, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await EndAsync(process, stopReading, reader).ConfigureAwait(false); // 시간 초과: 프로세스 트리를 끝낸다.
                var text = output.ToString();
                return new ProcessResult(-1, text, output.Truncated || process.OutputLineTruncated, string.Empty, true, null);
            }
            catch (OperationCanceledException)
            {
                await EndAsync(process, stopReading, reader).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task ReadAsync(IClaudeProcess process, OutputBuffer output, CancellationToken token)
    {
        try
        {
            await foreach (var line in process.ReadLinesAsync(token).ConfigureAwait(false))
            {
                output.AppendLine(CustomCliOutput.StripAnsi(line));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 취소했거나 프로세스가 끝나며 파이프가 닫혔다. 받은 만큼으로 이어간다.
        }
    }

    private static async Task EndAsync(IClaudeProcess process, CancellationTokenSource stopReading, Task reader)
    {
        process.Kill();
        await stopReading.CancelAsync().ConfigureAwait(false);
        await reader.ConfigureAwait(false);

        // 프로세스가 실제로 끝나기를 잠깐 기다린다(Windows 에서 폴더를 쥐고 있으면 임시 폴더를 지우지 못한다).
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 끝나지 않아도 정리를 이어간다.
        }
    }

    private static string ComposePrompt(List<string> messages, string prompt, JsonElement? schema)
    {
        var builder = new StringBuilder(messages[0]);
        if (messages.Count > 1)
        {
            builder.Append("\n\n[앞선 대화]");
            for (var i = 1; i < messages.Count; i++)
            {
                builder.Append(i % 2 == 1 ? "\n[사용자]\n" : "\n[도우미]\n").Append(messages[i]).Append('\n');
            }
        }

        builder.Append("\n\n[사용자]\n").Append(prompt);
        if (schema is { } s)
        {
            builder.Append("\n\n[출력 형식]\n답의 마지막에 아래 JSON 스키마를 따르는 JSON 객체 하나를 ```json 코드 블록으로 출력하세요. 코드 블록 밖에는 설명을 자유롭게 써도 됩니다.\n스키마:\n")
                .Append(s.GetRawText());
        }

        return builder.ToString();
    }

    private static void Commit(OpenAiConversationStore.Lease lease, string prompt, string output)
    {
        List<string> messages = [.. lease.Messages, prompt, output];
        lease.Commit(messages, messages.Sum(m => (long)m.Length), contextExceeded: false);
    }

    private static string FailureMessage(AiCustomCli cli, ProcessResult run)
    {
        var message = new StringBuilder($"{DisplayName(cli)} 이(가) 종료 코드 {run.ExitCode} 로 끝났습니다.");
        var tail = run.ErrorTail.Trim();
        if (tail.Length > 0)
        {
            message.Append('\n').Append(tail.Length > MaxErrorChars ? tail[^MaxErrorChars..] : tail);
        }

        // node 스크립트로 된 CLI 를 앱이 띄우면 PATH 에서 node 를 못 찾을 수 있다(Finder 로 띄운 앱의 PATH 에는 Homebrew 폴더가 없다).
        if (tail.Contains("node", StringComparison.OrdinalIgnoreCase) && tail.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            message.Append("\n실행 파일이 node 스크립트라면 node 와 같은 폴더에 있어야 합니다. 그렇지 않으면 node 를 부르는 래퍼 스크립트의 절대 경로를 실행 명령으로 쓰세요.");
        }

        return message.ToString();
    }

    private static string StructuredFailureMessage(AiCustomCli cli, bool found, IReadOnlyList<string> issues, string output, bool truncated)
    {
        var message = new StringBuilder(found
            ? $"{DisplayName(cli)} 이(가) 낸 JSON 이 요구한 형식과 맞지 않아 결과로 쓰지 않았습니다: {string.Join("; ", issues)}"
            : $"{DisplayName(cli)} 의 출력에서 요구한 형식의 JSON 결과를 찾지 못했습니다.");
        if (truncated)
        {
            message.Append("\n출력이 너무 길어 앞부분이 잘렸습니다.");
        }

        message.Append("\n이 CLI 가 낸 글:\n").Append(output.Length > MaxExcerptChars ? output[..MaxExcerptChars] + "…" : output);
        return message.ToString();
    }

    private sealed record ProcessResult(int ExitCode, string Output, bool OutputTruncated, string ErrorTail, bool TimedOut, string? StartError);

    /// <summary>
    /// 줄 단위 출력 모음. 상한을 넘으면 앞부분을 버리고 뒤를 남긴다(결과 JSON 은 보통 끝에 온다). 매 줄마다 앞을 깎지 않도록 여유를 두고 한꺼번에 깎는다.
    /// </summary>
    private sealed class OutputBuffer(int capacity)
    {
        private readonly StringBuilder _text = new();

        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > capacity + (capacity / 4))
                {
                    _text.Remove(0, _text.Length - capacity);
                    Truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_text)
            {
                if (_text.Length > capacity)
                {
                    _text.Remove(0, _text.Length - capacity);
                    Truncated = true;
                }

                return _text.ToString();
            }
        }
    }

    /// <summary>
    /// 실행마다 만드는 임시 작업 폴더(앱 데이터 밖, 시스템 임시 폴더 아래). 프로젝트 파일을 우연히 읽지 않게 할 뿐 격리가 아니다.
    /// Unix 에서는 폴더 0700·파일 0600 으로 만들어 사용자만 접근한다(Windows 는 권한을 따로 주지 않고 사용자 임시 폴더의 기본 권한을 따른다).
    /// 글을 파일로 넘기는 명령이면 이 폴더에 파일을 만든다. 끝나면 폴더째 지우고, 지우지 못한 폴더는 다음 실행 때 하루 지난 것부터 치운다.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private Workspace(string directory) => Directory = directory;

        public string Directory { get; }

        public string? PromptFile { get; private set; }

        public static Workspace Create(string root)
        {
            var path = Path.Combine(root, WorkspacePrefix + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
            {
                System.IO.Directory.CreateDirectory(path);
            }
            else
            {
                System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new Workspace(path);
        }

        /// <summary>앞선 실행이 지우지 못하고 남긴 폴더 중 하루가 지난 것을 지운다. 실패는 무시한다.</summary>
        public static void SweepStale(string root)
        {
            try
            {
                foreach (var stale in System.IO.Directory.EnumerateDirectories(root, WorkspacePrefix + "*")
                    .Where(d => DateTime.UtcNow - System.IO.Directory.GetCreationTimeUtc(d) > TimeSpan.FromDays(1)))
                {
                    TryDelete(stale, attempts: 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 임시 폴더를 훑지 못해도 실행에는 영향이 없다.
            }
        }

        public void WritePromptFile(string text)
        {
            var path = Path.Combine(Directory, "prompt.txt");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);
            stream.Write(new UTF8Encoding(false).GetBytes(text));
            PromptFile = path;
        }

        public void Dispose() => TryDelete(Directory, attempts: 3);

        // 막 끝낸 프로세스가 폴더를 놓기까지 잠깐 걸릴 수 있어(Windows) 몇 번 다시 시도한다.
        private static void TryDelete(string directory, int attempts)
        {
            for (var i = 0; i < attempts; i++)
            {
                try
                {
                    System.IO.Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (i + 1 < attempts)
                    {
                        Thread.Sleep(150);
                    }
                }
            }
        }
    }
}
