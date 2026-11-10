using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Platform.ExternalLoad;

/// <summary>
/// 외부 적재 도구 실행기(XL1, <see cref="IExternalProcessRunner"/>). 셸 없이(<c>UseShellExecute=false</c>) 창 없이 띄우고, 인자는 <see cref="ProcessStartInfo.ArgumentList"/> 로 넘긴다.
/// 표준 입력은 <see cref="ExternalProcessStart.StandardInput"/> 을 쓰고 바로 닫는다(UTF-8, BOM 없음 — 비밀번호 앞에 BOM 이 붙지 않게).
/// 표준 출력·오류는 비동기로 읽어 끝 64KB 만 남긴다. 시간 제한이 지나면 프로세스 트리를 끝내고 <see cref="ExternalProcessResult.TimedOut"/>,
/// 취소되면 프로세스 트리를 끝내고 끝날 때까지 기다린 뒤 <see cref="OperationCanceledException"/>.
/// </summary>
[OrandaService(typeof(IExternalProcessRunner))]
internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    /// <summary>출력마다 남기는 끝부분 글자 수.</summary>
    internal const int TailChars = 64 * 1024;

    /// <summary>끝낸 뒤 종료·출력 마감을 기다리는 시간(손자 프로세스가 출력 핸들을 쥐고 남는 경우 대비).</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(10);

    public async Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(start) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ExternalProcessException($"외부 도구를 실행하지 못했습니다({Path.GetFileName(start.FileName)}): {ex.Message}", ex);
        }

        var stdout = ReadTailAsync(process.StandardOutput);
        var stderr = ReadTailAsync(process.StandardError);
        var finished = false;
        try
        {
            await WriteInputAsync(process, start.StandardInput).ConfigureAwait(false);

            using var timeout = start.Timeout is { } limit ? new CancellationTokenSource(limit) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout?.Token ?? CancellationToken.None);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await KillAndWaitAsync(process).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    finished = true;
                    await DrainAsync(stdout, stderr).ConfigureAwait(false);
                    throw new OperationCanceledException(ct);
                }

                timedOut = true;
            }

            finished = true;
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            return new ExternalProcessResult(exitCode, Completed(stdout), Completed(stderr), timedOut);
        }
        finally
        {
            if (!finished)
            {
                // 예상하지 못한 예외로 나가더라도 프로세스를 남기지 않는다.
                await KillAndWaitAsync(process).ConfigureAwait(false);
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ExternalProcessStart start)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(start.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = start.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var argument in start.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in start.Environment)
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

        return info;
    }

    private static async Task WriteInputAsync(Process process, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // 도구가 입력을 읽지 않고 이미 끝났다(파이프 끊김) — 결과는 종료 코드·출력으로 본다.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 위와 같다.
            }
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // 이미 끝났거나 일부 자식을 못 끝냈다 — 아래 기다림이 한도에서 멈춘다.
        }

        using var wait = new CancellationTokenSource(DrainLimit);
        try
        {
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 끝나지 않아도 더 기다리지 않는다.
        }
    }

    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        var both = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(both, Task.Delay(DrainLimit)).ConfigureAwait(false);
    }

    private static string Completed(Task<string> reader) =>
        reader.IsCompletedSuccessfully ? reader.Result : string.Empty;

    /// <summary>스트림을 끝까지 읽어 마지막 <see cref="TailChars"/> 글자만 남긴다.</summary>
    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[8192];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > TailChars * 2)
                {
                    tail.Remove(0, tail.Length - TailChars);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 프로세스를 끝내 스트림이 닫혔다 — 읽은 데까지 둔다.
        }

        if (tail.Length > TailChars)
        {
            tail.Remove(0, tail.Length - TailChars);
        }

        return tail.ToString();
    }
}
