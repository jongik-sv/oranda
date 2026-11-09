using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Core.Composition.Dispatch;

/// <summary>
/// 세션의 DBMS 로 구현을 골라 넘긴다(D-046). 구현 등록 = <c>[OrandaService(typeof(ISqlPlusCommandProcessor), Dbms = …)]</c>.
/// 스크립트 지시(<see cref="IScriptDirectives"/>)도 같은 구현으로 넘긴다 — 실행 엔진이 받는 처리기가 이 디스패처라서(K0).
/// 구현이 없거나 지시를 구현하지 않은 DBMS 는 「지시 없음」(멈추지 않음·DEFINE 없음).
/// </summary>
[OrandaService(typeof(ISqlPlusCommandProcessor))]
public sealed class SqlPlusCommandProcessorDispatcher : ISqlPlusCommandProcessor, IScriptDirectives
{
    private readonly IDbmsServiceSelector _selector;

    public SqlPlusCommandProcessorDispatcher(IDbmsServiceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }

    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct) =>
        _selector.For<ISqlPlusCommandProcessor>(s).ExecuteAsync(s, stmt, ct);

    public bool ExitOnError(ISession s) => Directives(s)?.ExitOnError(s) ?? false;

    public Task ApplyOnErrorTransactionAsync(ISession s, CancellationToken ct) =>
        Directives(s)?.ApplyOnErrorTransactionAsync(s, ct) ?? Task.CompletedTask;

    public string? GetDefine(ISession s, string name) => Directives(s)?.GetDefine(s, name);

    public void SetDefine(ISession s, string name, string value) => Directives(s)?.SetDefine(s, name, value);

    private IScriptDirectives? Directives(ISession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return _selector.Find<ISqlPlusCommandProcessor>(s.Profile.Dbms) as IScriptDirectives;
    }
}
