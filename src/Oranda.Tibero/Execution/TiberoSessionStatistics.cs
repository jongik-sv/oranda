using Oranda.Core.Execution.SqlPlus;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 세션 통계의 출처(D5 = 켠다): 실행 전후 <c>v$mystat</c> 값의 차이. Tibero 의 <c>v$mystat</c> 에는 <c>NAME</c> 열이 직접 있고
/// Oracle 문장(<c>v$statname</c> 과 <c>statistic#</c> 로 잇기)은 TBR-8026 이다. 이름 대응·실측 = docs/design/tibero-driver-exec.md 6장.
/// </summary>
internal static class TiberoSessionStatistics
{
    internal const string Query = "SELECT name, value FROM v$mystat WHERE value <> 0";

    /// <summary>
    /// 화면에 보여 줄 통계(Oracle 목록과 같은 순서, Tibero 이름). 받은 바이트(<c>bytes received via SQL*Net from client</c>)는
    /// Tibero 에 대응 통계가 없어 뺀다. 왕복 횟수 자리는 가장 가까운 <c>user calls</c>.
    /// </summary>
    internal static readonly string[] Interesting =
    [
        "logical reads",
        "current block gets",
        "consistent block gets",
        "physical reads",
        "redo log size",
        "the number of user commits performed",
        "parse count (total)",
        "parse count (hard)",
        "execute count",
        "sorts (memory)",
        "sorts (disk)",
        "sent message size (byte)",
        "user calls",
    ];

    internal static SessionStatisticsSource Source { get; } = new(Query, Interesting);
}
