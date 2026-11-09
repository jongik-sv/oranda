using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Execution.SqlPlus;
using Oranda.Core.Language;

namespace Oranda.Oracle.SqlPlus;

/// <summary>
/// Oracle 의 SQL*Plus 처리기: DBMS 에 매이지 않는 <see cref="SqlPlusCommandProcessor"/> 에 Oracle 세션 통계(<c>v$mystat</c>)를 붙여 등록한다.
/// 소유: TSK-06-06.
/// </summary>
[OrandaService(typeof(ISqlPlusCommandProcessor), Dbms = Dbms.Oracle)]
internal sealed class OracleSqlPlusCommandProcessor : SqlPlusCommandProcessor
{
    public OracleSqlPlusCommandProcessor(IServiceProvider services)
        : base(services, OracleSessionStatistics.Source)
    {
    }

    internal OracleSqlPlusCommandProcessor(Func<IExecutionService> execution, Func<ILanguageService> language, Func<IEventBus?>? events = null)
        : base(execution, language, events, OracleSessionStatistics.Source)
    {
    }
}
