using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>연결 하나를 끝까지 맡는다(읽기 루프 포함). 연결이 닫히면 돌아온다. 예외를 던지면 그 연결만 닫는다.</summary>
internal delegate Task AgentConnectionHandler(AgentRpcChannel channel, CancellationToken cancellationToken);

/// <summary>
/// 현재 사용자 전용 명명 파이프 서버. 연결은 <see cref="MaxConnections"/> 개까지 동시에 받고 연결마다 따로 처리한다
/// (여러 AI 실행이 겹쳐도 서로 기다리지 않는다). 한 연결 안의 요청은 차례로 처리된다.
/// 고정 이름을 먼저 시도하고(FirstPipeInstance), 이미 다른 앱이 쥐고 있으면 <c>&lt;이름&gt;-&lt;pid&gt;</c> 로 연다.
/// 다음 연결을 받을 인스턴스는 지금 연결을 처리하기 <b>전에</b> 만들어 이름을 비워 두는 틈이 없다.
/// 이름 규칙은 주입받는다(<see cref="Oranda.Core.Ai.Protocol.AgentPipeName"/>).
/// </summary>
internal sealed class AgentPipeServer : IAsyncDisposable
{
    internal const int MaxConnections = 8;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Func<string, int?, string> _nameOf;
    private readonly AgentConnectionHandler _handler;
    private readonly string _userKey;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Task? _loop;

    /// <param name="nameOf">(사용자 키, 두 번째 앱이면 프로세스 id, 첫 앱이면 null) → 파이프 이름. 이름이 절대 경로면 소켓 경로로 쓰고 그 폴더를 0700 으로 만든다.</param>
    /// <param name="handler">연결 처리기.</param>
    /// <param name="userKey">사용자 키. null 이면 현재 사용자(<see cref="CurrentUser.GetKey"/>).</param>
    public AgentPipeServer(Func<string, int?, string> nameOf, AgentConnectionHandler handler, string? userKey = null)
    {
        _nameOf = nameOf;
        _handler = handler;
        _userKey = userKey ?? CurrentUser.GetKey();
    }

    /// <summary>실제로 연 파이프 이름(<see cref="Start"/> 뒤에 유효).</summary>
    public string PipeName { get; private set; } = string.Empty;

    /// <summary>연결을 받는 중인가. 수락 루프가 죽었으면 false(이름은 남아 있어도 접속할 수 없다).</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    public string Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("이미 시작했습니다.");
        }

        var name = _nameOf(_userKey, null);
        var first = TryCreate(name, firstInstance: true);
        if (first is null)
        {
            name = _nameOf(_userKey, Environment.ProcessId);
            first = TryCreate(name, firstInstance: true)
                ?? throw new IOException($"파이프를 열지 못했습니다: {name}");
        }

        PipeName = name;
        _loop = Task.Run(() => AcceptLoopAsync(name, first, _stop.Token));
        return name;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await AwaitQuietlyAsync(_loop).ConfigureAwait(false);
        }

        await AwaitQuietlyAsync(Task.WhenAll(_connections.Keys)).ConfigureAwait(false);
        _stop.Dispose();
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 끄는 중이다. 어떤 실패든 남은 정리를 막지 않는다.
        }
    }

    private static NamedPipeServerStream? TryCreate(string name, bool firstInstance)
    {
        if (Path.IsPathRooted(name) && Path.GetDirectoryName(name) is { Length: > 0 } directory)
        {
            PrivateDirectory.Ensure(directory);
        }

        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        try
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync(string name, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? waiting = pipe;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                waiting ??= await CreateNextAsync(name, cancellationToken).ConfigureAwait(false);
                var connected = waiting;
                waiting = null;
                try
                {
                    await connected.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 붙자마자 끊은 클라이언트나 피어 검사 실패. 그 연결만 버린다. 이름이 비지 않도록 다음 인스턴스를 먼저 만든다
                    // (한도가 차서 못 만들면 버린 뒤 다음 바퀴에서 만든다).
                    waiting = TryCreate(name, firstInstance: false);
                    await connected.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                // 이 연결을 처리하기 전에 다음 인스턴스를 만들어 이름이 비는 틈을 없앤다.
                waiting = TryCreate(name, firstInstance: false);
                TrackConnection(connected, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 서버를 끈다.
        }
        finally
        {
            if (waiting is not null)
            {
                await waiting.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>다음 인스턴스를 만든다. 동시 연결 한도가 차 있으면 자리가 날 때까지 잠시 기다렸다 다시 시도한다.</summary>
    private static async Task<NamedPipeServerStream> CreateNextAsync(string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryCreate(name, firstInstance: false) is { } created)
            {
                return created;
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void TrackConnection(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var task = Task.Run(() => ServeConnectionAsync(pipe, cancellationToken), CancellationToken.None);
        _connections.TryAdd(task, 0);
        _ = task.ContinueWith(t => _connections.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            using var channel = new AgentRpcChannel(pipe);
            try
            {
                await _handler(channel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // 상대가 끊었거나, 한 줄 한도를 넘었거나, 처리기가 예외를 던졌다(처리기 안의 취소 포함). 이 연결만 닫는다.
            }
            catch (OperationCanceledException)
            {
                // 서버를 끈다.
            }
        }
    }
}
