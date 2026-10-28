using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Oranda.Platform.Ai.Cli;

/// <summary>claude 를 한 번 띄우는 데 필요한 값.</summary>
/// <param name="ExecutablePath">claude 실행 파일 경로.</param>
/// <param name="Arguments"><see cref="ClaudeCliArguments.Build"/> 의 결과.</param>
/// <param name="WorkingDirectory">빈 작업 폴더(프로젝트 설정·파일을 읽지 못하게 한다).</param>
/// <param name="Prompt">표준 입력으로 쓰고 닫을 프롬프트.</param>
internal sealed record ClaudeLaunch(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory, string Prompt);

/// <summary>실행 중인 claude. 시험은 가짜로 바꾼다.</summary>
internal interface IClaudeProcess : IAsyncDisposable
{
    /// <summary>표준 출력을 한 줄씩. 프로세스가 끝나면 마친다.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>표준 오류의 마지막 부분(진단용).</summary>
    string StandardErrorTail { get; }

    /// <summary>프로세스 트리를 끝낸다. 이미 끝났으면 아무 일도 하지 않는다.</summary>
    void Kill();
}

internal interface IClaudeProcessLauncher
{
    IClaudeProcess Start(ClaudeLaunch launch);
}

internal sealed class SystemClaudeProcessLauncher : IClaudeProcessLauncher
{
    public IClaudeProcess Start(ClaudeLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
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

        return new SystemClaudeProcess(process, launch.Prompt);
    }
}

internal sealed class SystemClaudeProcess : IClaudeProcess
{
    private const int MaxErrorTailChars = 2000;

    private readonly Process _process;
    private readonly object _errorLock = new();
    private readonly StringBuilder _errorTail = new();
    private readonly Task _stdin;
    private readonly Task _stderr;

    public SystemClaudeProcess(Process process, string prompt)
    {
        _process = process;
        _stdin = WriteAndCloseStdinAsync(prompt);
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

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 이미 끝났거나 끝나는 중이다.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Task.WhenAny(Task.WhenAll(_stdin, _stderr), Task.Delay(1000)).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task WriteAndCloseStdinAsync(string prompt)
    {
        try
        {
            await _process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 프롬프트를 다 읽기 전에 claude 가 끝났다. 결과는 종료 코드와 출력으로 드러난다.
        }
        finally
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 이미 닫혔다.
            }
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_errorLock)
                {
                    _errorTail.AppendLine(line);
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
