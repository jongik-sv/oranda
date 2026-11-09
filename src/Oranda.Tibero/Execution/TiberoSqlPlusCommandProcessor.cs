using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.SqlPlus;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 세션의 SQL*Plus 명령 처리기(<c>Dbms = Tibero</c> 키 등록). 동작은 Core <see cref="SqlPlusCommandProcessor"/> 그대로이고,
/// 세션 통계(SET STATISTICS)는 Tibero 의 통계 이름을 실측하기 전이라 끈다(D5: 없음 + 이유). 소유: tb-base K2(결정30).
/// </summary>
[OrandaService(typeof(ISqlPlusCommandProcessor), Dbms = Dbms.Tibero)]
internal sealed class TiberoSqlPlusCommandProcessor : SqlPlusCommandProcessor
{
    public TiberoSqlPlusCommandProcessor(IServiceProvider services)
        : base(services, statistics: null)
    {
    }
}
