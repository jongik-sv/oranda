using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary>
/// 요청 때마다 붙을 곳을 정하는 통로. 처음 요청에서 통로·토큰을 정하고(<see cref="BridgeTargetResolver"/>) 실제 통로를 만든다.
/// 앱에 닿지 못하거나 연결이 끝나면(종료 코드 3 의 오류) 그 통로를 버리고 다음 요청에서 다시 찾는다 —
/// 오래 떠 있는 <c>orandactl mcp</c> 가 앱을 다시 켜거나 연결을 다시 켠 뒤에도 이어지게 한다(pid·토큰이 바뀐다).
/// </summary>
internal sealed class DiscoveringBridgeClient(Func<BridgeTarget> resolve, Func<BridgeTarget, IBridgeClient> create) : IBridgeClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBridgeClient? _inner;

    public Task<IReadOnlyList<AgentToolInfo>> ListToolsAsync(AgentClientInfo client, CancellationToken cancellationToken) =>
        UseAsync(inner => inner.ListToolsAsync(client, cancellationToken), cancellationToken);

    public Task<AgentCallToolResult> CallToolAsync(AgentClientInfo client, string name, JsonNode? arguments, CancellationToken cancellationToken) =>
        UseAsync(inner => inner.CallToolAsync(client, name, arguments, cancellationToken), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_inner is { } inner)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            _inner = null;
        }

        _gate.Dispose();
    }

    private async Task<T> UseAsync<T>(Func<IBridgeClient, Task<T>> call, CancellationToken cancellationToken)
    {
        IBridgeClient inner;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            inner = _inner ??= create(resolve());
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await call(inner).ConfigureAwait(false);
        }
        catch (BridgeException ex) when (AgentCliExit.For(ex.Code) == AgentCliExit.Unreachable)
        {
            await DropAsync(inner).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DropAsync(IBridgeClient inner)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_inner, inner))
            {
                _inner = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        await inner.DisposeAsync().ConfigureAwait(false);
    }
}
