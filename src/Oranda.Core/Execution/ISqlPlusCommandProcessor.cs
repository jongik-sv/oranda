using Oranda.Core.Sessions;

namespace Oranda.Core.Execution;

/// <summary>
/// SQL*Plus 명령(<see cref="StatementKind.SqlPlus"/>: SET SERVEROUTPUT, DEFINE, PROMPT, @파일, SPOOL, WHENEVER SQLERROR) 처리기.
/// 실행 엔진이 해당 종류의 문장을 이리로 넘긴다. 상태(DEFINE 값 등)는 세션별로 처리기가 갖는다. 구현: TSK-06-06.
/// </summary>
public interface ISqlPlusCommandProcessor
{
    Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct);
}
