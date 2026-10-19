using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.SqlPlus;

/// <summary>SQL*Plus 명령 처리(SET SERVEROUTPUT, DEFINE, PROMPT, @파일, SPOOL, WHENEVER SQLERROR). 소유: TSK-06-06. 빈 틀 — 기능 Task 가 구현한다.</summary>
[OrandaService(typeof(ISqlPlusCommandProcessor))]
internal sealed class SqlPlusCommandProcessor : ISqlPlusCommandProcessor
{
    public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, CancellationToken ct)
        => throw new NotImplementedException("TSK-06-06");
}
