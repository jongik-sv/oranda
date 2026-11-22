namespace Oranda.Platform.Ai.Setup;

/// <summary>
/// 뒤에서 도는 <c>claude auth login</c> 하나. 출력에서 로그인 주소를 찾아 <see cref="Url"/> 로 알린다.
/// 로그인이 끝났는지는 출력이 아니라 <see cref="ClaudeSetupEngine.StatusAsync"/> 로 본다.
/// 버리면(Dispose) 프로세스 트리를 끝낸다(claude 가 띄운 브라우저가 그 트리에 있으면 같이 끝날 수 있다 — 로그인이 끝난 뒤에만 버린다면 문제없다).
/// </summary>
internal sealed class ClaudeLoginSession : IAsyncDisposable
{
    // 프로세스가 끝난 뒤 남은 출력을 마저 읽는 시간. claude 가 띄운 브라우저가 출력 관을 물려받아 쥐고 있어도 기다림이 끝난다.
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    private readonly ISetupProcess _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string?> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private readonly Task<int> _exited;
    private int _disposed;

    public ClaudeLoginSession(ISetupProcess process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _reader = ReadAsync();
        _exited = WaitExitAsync();
    }

    /// <summary>로그인 주소(브라우저가 안 열릴 때 복사용). claude 가 주소 없이 끝나면 null.</summary>
    public Task<string?> Url => _url.Task;

    /// <summary>claude 가 끝나면(로그인 마침·실패·끊기) 종료 코드로 끝나는 작업. 끊어서 모르면 -1. 예외를 내지 않는다.</summary>
    public Task<int> Exited => _exited;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.Kill();
            await _stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_reader, _exited).ConfigureAwait(false);
        }
        finally
        {
            await _process.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var line in _process.ReadLinesAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!_url.Task.IsCompleted && ClaudeSetupOutput.LoginUrl(line.Text) is { } url)
                {
                    _url.TrySetResult(url);
                }
            }
        }
        catch (Exception)
        {
            // 끊었거나 흐름이 깨졌다. 주소는 아래에서 null 로 닫는다.
        }
        finally
        {
            _url.TrySetResult(null);
        }
    }

    private async Task<int> WaitExitAsync()
    {
        int code;
        try
        {
            code = await _process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            code = -1; // 끊었다.
        }

        // 출력 관이 브라우저 등에 물려 닫히지 않아도 주소 기다림이 끝나게 한다.
        await Task.WhenAny(_reader, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);
        _url.TrySetResult(null);
        return code;
    }
}
