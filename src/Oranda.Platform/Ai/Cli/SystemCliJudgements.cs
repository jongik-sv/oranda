using System.Collections.Concurrent;
using Oranda.Core.Ai.Gate;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 한 실행의 게이트 판단 큐. 게이트가 도구 호출을 끝낼 때마다 이 실행의 판단(허용·가림·거부 포함)을 도구 이름별로 호출 순서대로 쌓고,
/// CLI 가 같은 도구의 결과를 낼 때 하나씩 꺼내 도구 카드에 그대로 옮긴다. <see cref="Dispose"/> 가 구독을 해제한다.
/// </summary>
internal sealed class SystemCliJudgements : IDisposable
{
    private readonly IAgentGate _gate;
    private readonly string _runId;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentToolActivityEventArgs>> _byTool = new(StringComparer.Ordinal);
    private int _disposed;

    public SystemCliJudgements(IAgentGate gate, string runId)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _runId = runId;
        _gate.ToolCompleted += OnToolCompleted;
    }

    /// <summary>이 도구의 다음 판단을 꺼낸다. 게이트를 거치지 않았으면(권한 거부·연결 실패) false.</summary>
    public bool TryTake(string toolName, out AgentToolActivityEventArgs completed)
    {
        if (_byTool.TryGetValue(toolName, out var queue) && queue.TryDequeue(out var found))
        {
            completed = found;
            return true;
        }

        completed = null!;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.ToolCompleted -= OnToolCompleted;
        }
    }

    private void OnToolCompleted(object? sender, AgentToolActivityEventArgs e)
    {
        if (e.RunId == _runId)
        {
            _byTool.GetOrAdd(e.ToolName, _ => new ConcurrentQueue<AgentToolActivityEventArgs>()).Enqueue(e);
        }
    }
}
