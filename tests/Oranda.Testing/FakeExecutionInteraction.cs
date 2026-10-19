using Oranda.Core.Execution;

namespace Oranda.Testing;

/// <summary>화면 없이 정해진 답을 돌려주는 실행 대화 창구. 물은 내용을 기록한다.</summary>
public sealed class FakeExecutionInteraction : IExecutionInteraction
{
    private readonly List<PolicyDecision> _confirmations = [];

    public IReadOnlyList<PolicyDecision> Confirmations => _confirmations;

    public bool ConfirmAnswer { get; set; } = true;

    public IReadOnlyDictionary<string, string>? SubstitutionAnswer { get; set; } = new Dictionary<string, string>();

    public ScriptErrorAction ScriptErrorAnswer { get; set; } = ScriptErrorAction.Stop;

    public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct)
    {
        _confirmations.Add(decision);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult(SubstitutionAnswer);

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
        Task.FromResult(ScriptErrorAnswer);
}
