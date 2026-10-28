using System.Text.Json.Nodes;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Platform.Tests.Ai;

internal sealed class FakeRunTokens : IAgentRunTokens
{
    private readonly Dictionary<string, AgentSession> _byToken = [];

    public List<string> Issued { get; } = [];

    public List<string> Revoked { get; } = [];

    public string Issue(AgentSession session)
    {
        var token = AgentRunToken.Generate();
        _byToken[token] = session;
        Issued.Add(token);
        return token;
    }

    public AgentSession? Resolve(string token) => _byToken.GetValueOrDefault(token);

    public void Revoke(string token)
    {
        Revoked.Add(token);
        if (_byToken.Remove(token, out var session))
        {
            session.Revoke();
        }
    }
}

internal sealed class FakeGate : IAgentGate
{
    public string? LastTool { get; private set; }

    public JsonNode? LastArguments { get; private set; }

    public AgentSession? LastSession { get; private set; }

    public bool Throw { get; set; }

    public bool ThrowCancel { get; set; }

    public bool HugeResult { get; set; }

    public bool QuoteResult { get; set; }

    public TaskCompletionSource? Block { get; set; }

    public event EventHandler<AgentToolActivityEventArgs>? ToolCompleted;

    public void Raise(AgentToolActivityEventArgs e) => ToolCompleted?.Invoke(this, e);

    public bool HasSubscribers => ToolCompleted is not null;

    public IReadOnlyList<AgentToolInfo> ListTools(AgentSession session) =>
        [new AgentToolInfo("describe_object", "describe", new JsonObject { ["type"] = "object" })];

    public async Task<AgentGateResponse> CallToolAsync(AgentSession session, string toolName, JsonNode? arguments, CancellationToken ct)
    {
        if (Block is { } block)
        {
            await block.Task.WaitAsync(ct);
        }

        if (Throw)
        {
            throw new InvalidOperationException("secret detail");
        }

        if (ThrowCancel)
        {
            throw new OperationCanceledException("gate internal timeout");
        }

        (LastSession, LastTool, LastArguments) = (session, toolName, arguments);
        var text = HugeResult ? new string('x', AgentRpc.MaxMessageBytes) : QuoteResult ? new string('"', 1_000_000) : "{\"ok\":true}";
        return new AgentGateResponse(new AgentCallToolResult(false, text), AiToolJudgement.Allowed);
    }
}
