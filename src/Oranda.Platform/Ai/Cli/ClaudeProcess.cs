using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Oranda.Platform.Ai.Profiles;

namespace Oranda.Platform.Ai.Cli;

/// <summary>claude 를 한 번 띄우는 데 필요한 값.</summary>
/// <param name="ExecutablePath">claude 실행 파일 경로.</param>
/// <param name="Arguments"><see cref="ClaudeCliArguments.Build"/> 의 결과.</param>
/// <param name="WorkingDirectory">빈 작업 폴더(프로젝트 설정·파일을 읽지 못하게 한다).</param>
/// <param name="StandardInput">표준 입력으로 쓰고 닫을 글(<see cref="ClaudeStreamInput.Build"/> 가 만든 stream-json 한 줄).</param>
/// <param name="Environment">자식 프로세스 환경 변수 덮어쓰기. 값이 null 이면 부모에게서 물려받은 변수를 지운다. 비어 있으면 부모 환경 그대로다.</param>
/// <param name="IsolateEnvironment">true 면 부모 환경에서 <see cref="ClaudeProfileEnvironment.IsScrubbed"/> 이름을 물려받지 않는다(프로필 실행). false 는 구독 경로로, 부모 환경을 그대로 쓴다.</param>
/// <param name="Secret">표준 오류에 나와도 가려야 하는 값(프로필 API 키). 줄 단위로 가린 뒤 끝부분을 쌓으므로, 자르는 경계에 걸쳐 일부가 남지 않는다.</param>
/// <param name="MaxLineChars">null 이 아니면 표준 출력·표준 오류의 한 줄을 이 글자 수로 제한한다(넘으면 앞부분을 버리고 뒤를 남긴다, 기타 CLI 전용).
/// null 이면 줄바꿈이 올 때까지 한 줄을 그대로 읽는다(기존 Claude·Codex·agy 경로).</param>
internal sealed record ClaudeLaunch(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardInput,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? Secret = null,
    bool IsolateEnvironment = false,
    int? MaxLineChars = null)
{
    // 기본 출력(ToString·로그·디버거)에 키(환경 변수 값)와 사용자 글(표준 입력)이 나오지 않게 한다.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ExecutablePath = ").Append(ExecutablePath)
            .Append(", Arguments = ").Append(Arguments.Count)
            .Append(", Environment = ").Append(Environment?.Count ?? 0);
        return true;
    }
}

/// <summary>실행 중인 claude. 시험은 가짜로 바꾼다.</summary>
internal interface IClaudeProcess : IAsyncDisposable
{
    /// <summary>표준 출력을 한 줄씩. 프로세스가 끝나면 마친다.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>표준 오류의 마지막 부분(진단용).</summary>
    string StandardErrorTail { get; }

    /// <summary>표준 출력의 한 줄이 <see cref="ClaudeLaunch.MaxLineChars"/> 를 넘어 앞부분을 버린 적이 있다. 상한을 두지 않은 프로세스는 항상 false.</summary>
    bool OutputLineTruncated => false;

    /// <summary>프로세스 트리를 끝낸다. 이미 끝났으면 아무 일도 하지 않는다.</summary>
    void Kill();
}

internal interface IClaudeProcessLauncher
{
    IClaudeProcess Start(ClaudeLaunch launch);
}

internal sealed class SystemClaudeProcessLauncher : IClaudeProcessLauncher
{
    /// <summary>실행 정보를 만든다(시험이 실제 <see cref="ProcessStartInfo"/> 수준에서 환경 변수 조합을 확인한다).</summary>
    internal static ProcessStartInfo CreateStartInfo(ClaudeLaunch launch)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(launch.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        ApplyEnvironment(info.Environment, launch);
        return info;
    }

    /// <summary>
    /// 자식 환경을 정한다: 실행 파일 폴더를 PATH 앞에 두고, <see cref="ClaudeLaunch.Environment"/> 를 적용한다(null 값은 지운다).
    /// PATH 밖(흔한 설치 위치)에서 찾은 claude 면 그 폴더를 자식 PATH 앞에 둔다: npm 설치의 claude 는 같은 폴더의 node 를 찾고,
    /// macOS 에서 Finder 로 띄운 앱의 PATH 에는 Homebrew 폴더가 없다.
    /// </summary>
    internal static void ApplyEnvironment(IDictionary<string, string?> environment, ClaudeLaunch launch)
    {
        if (Path.GetDirectoryName(launch.ExecutablePath) is { Length: > 0 } directory)
        {
            var path = environment.TryGetValue("PATH", out var current) ? current ?? string.Empty : string.Empty;
            if (!path.Split(Path.PathSeparator).Contains(directory, StringComparer.Ordinal))
            {
                environment["PATH"] = path.Length == 0 ? directory : directory + Path.PathSeparator + path;
            }
        }

        if (launch.IsolateEnvironment)
        {
            foreach (var name in environment.Keys.Where(ClaudeProfileEnvironment.IsScrubbed).ToList())
            {
                environment.Remove(name);
            }
        }

        foreach (var (name, value) in launch.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }
    }

    public IClaudeProcess Start(ClaudeLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var info = CreateStartInfo(launch);

        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new SystemClaudeProcess(process, launch.StandardInput, launch.Secret, launch.MaxLineChars);
    }
}

internal sealed class SystemClaudeProcess : IClaudeProcess
{
    private const int MaxErrorTailChars = 2000;

    // 줄 상한을 둘 때 표준 오류 한 줄에 허용하는 글자 수(꼬리 MaxErrorTailChars 보다 넉넉히).
    private const int MaxErrorLineChars = MaxErrorTailChars * 4;

    private readonly Process _process;
    private readonly object _errorLock = new();
    private readonly StringBuilder _errorTail = new();
    private readonly Task _stdin;
    private readonly Task _stderr;
    private readonly string? _secret;
    private readonly BoundedLineReader? _boundedOutput;
    private readonly BoundedLineReader? _boundedError;

    public SystemClaudeProcess(Process process, string standardInput, string? secret = null, int? maxLineChars = null)
    {
        _process = process;
        _secret = secret;
        if (maxLineChars is { } max)
        {
            _boundedOutput = new BoundedLineReader(process.StandardOutput, max);
            _boundedError = new BoundedLineReader(process.StandardError, MaxErrorLineChars);
        }

        _stdin = WriteAndCloseStdinAsync(standardInput);
        _stderr = DrainStandardErrorAsync();
    }

    public string StandardErrorTail
    {
        get
        {
            lock (_errorLock)
            {
                return _errorTail.ToString();
            }
        }
    }

    public bool OutputLineTruncated => _boundedOutput?.Truncated ?? false;

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await ReadOutputLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    private ValueTask<string?> ReadOutputLineAsync(CancellationToken cancellationToken) =>
        _boundedOutput is { } bounded
            ? bounded.ReadLineAsync(cancellationToken)
            : _process.StandardOutput.ReadLineAsync(cancellationToken);

    private ValueTask<string?> ReadErrorLineAsync() =>
        _boundedError is { } bounded
            ? bounded.ReadLineAsync(CancellationToken.None)
            : new ValueTask<string?>(_process.StandardError.ReadLineAsync());

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAny(_stderr, Task.Delay(500, cancellationToken)).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // 이미 끝났거나 끝나는 중이다(트리의 일부 자손을 끝내지 못한 AggregateException 포함).
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Task.WhenAny(Task.WhenAll(_stdin, _stderr), Task.Delay(1000)).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task WriteAndCloseStdinAsync(string standardInput)
    {
        try
        {
            await _process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 입력을 다 읽기 전에 claude 가 끝났거나(깨진 파이프) 취소로 프로세스가 정리됐다. 결과는 종료 코드와 출력으로 드러난다.
        }
        finally
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 이미 닫혔다.
            }
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        try
        {
            while (await ReadErrorLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_errorLock)
                {
                    _errorTail.AppendLine(SecretRedactor.Redact(line, _secret));
                    if (_errorTail.Length > MaxErrorTailChars)
                    {
                        _errorTail.Remove(0, _errorTail.Length - MaxErrorTailChars);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 프로세스가 끝나며 스트림이 닫혔다.
        }
    }
}
