using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Oranda.Platform.Ai.Setup;

/// <summary>설치·로그인·상태 확인에 띄우는 프로세스 하나.</summary>
/// <param name="FileName">실행 파일.</param>
/// <param name="Arguments">인자(셸을 거치지 않고 그대로 넘긴다).</param>
/// <param name="Environment">환경 변수 덮어쓰기. 값이 null 이면 물려받은 변수를 지운다.</param>
/// <param name="WorkingDirectory">작업 폴더. null 이면 임시 폴더.</param>
internal sealed record SetupLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? WorkingDirectory = null);

/// <summary>출력 한 줄. <paramref name="IsError"/> 는 표준 오류에서 온 줄.</summary>
internal sealed record SetupLine(string Text, bool IsError);

/// <summary>실행 중인 프로세스. 표준 입력은 시작하자마자 닫는다. 시험은 가짜로 바꾼다.</summary>
internal interface ISetupProcess : IAsyncDisposable
{
    /// <summary>표준 출력·표준 오류를 온 차례대로 한 줄씩. 두 흐름이 모두 닫히면 마친다.</summary>
    IAsyncEnumerable<SetupLine> ReadLinesAsync(CancellationToken cancellationToken);

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>프로세스 트리를 끝낸다. 이미 끝났으면 아무 일도 하지 않는다.</summary>
    void Kill();
}

internal interface ISetupProcessRunner
{
    /// <summary>프로세스를 띄운다. 실행 파일이 없으면 예외(<see cref="System.ComponentModel.Win32Exception"/> 등)를 낸다.</summary>
    ISetupProcess Start(SetupLaunch launch);
}

/// <summary>실제 프로세스를 숨은 창으로 띄우는 실행기.</summary>
internal sealed class SystemSetupProcessRunner : ISetupProcessRunner
{
    public ISetupProcess Start(SetupLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory ?? Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in launch.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
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

        return new SystemSetupProcess(process);
    }
}

internal sealed class SystemSetupProcess : ISetupProcess
{
    private readonly Process _process;
    private readonly Channel<SetupLine> _lines = Channel.CreateUnbounded<SetupLine>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pumps;

    public SystemSetupProcess(Process process)
    {
        _process = process;
        CloseStandardInput();
        _pumps = PumpBothAsync();
    }

    public async IAsyncEnumerable<SetupLine> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
            // 이미 끝났거나 끝나는 중이다.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        using (var delayCancel = new CancellationTokenSource())
        {
            await Task.WhenAny(_pumps, Task.Delay(1000, delayCancel.Token)).ConfigureAwait(false);
            await delayCancel.CancelAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private void CloseStandardInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // 이미 끝났다.
        }
    }

    private async Task PumpBothAsync()
    {
        try
        {
            await Task.WhenAll(PumpAsync(_process.StandardOutput, isError: false), PumpAsync(_process.StandardError, isError: true)).ConfigureAwait(false);
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    private async Task PumpAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _lines.Writer.TryWrite(new SetupLine(line, isError));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 프로세스가 끝나며 흐름이 닫혔다.
        }
    }
}
