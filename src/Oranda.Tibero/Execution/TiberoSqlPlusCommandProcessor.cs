using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.SqlPlus;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 세션의 SQL*Plus 명령 처리기(<c>Dbms = Tibero</c> 키 등록). 동작은 Core <see cref="SqlPlusCommandProcessor"/> 그대로이고,
/// 세션 통계(SET STATISTICS)는 <see cref="TiberoSessionStatistics"/>(<c>v$mystat</c> 의 Tibero 이름, D5 = 켠다). 소유: tb-base K2(결정30), 통계 = tb-exec D5.
/// </summary>
[OrandaService(typeof(ISqlPlusCommandProcessor), Dbms = Dbms.Tibero)]
internal sealed class TiberoSqlPlusCommandProcessor : SqlPlusCommandProcessor
{
    public TiberoSqlPlusCommandProcessor(IServiceProvider services)
        : base(services, TiberoSessionStatistics.Source)
    {
    }
}
