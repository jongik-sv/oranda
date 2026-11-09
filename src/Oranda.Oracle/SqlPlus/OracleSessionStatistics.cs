using Oranda.Core.Execution.SqlPlus;

namespace Oranda.Oracle.SqlPlus;

/// <summary>Oracle 세션 통계의 출처: 실행 전후 <c>v$mystat</c> 값의 차이. 읽기 전용 동적 뷰만 쓰며 라이선스 팩이 필요 없다.</summary>
internal static class OracleSessionStatistics
{
    private const string Query =
        "SELECT n.name, s.value FROM v$mystat s JOIN v$statname n ON n.statistic# = s.statistic# WHERE s.value <> 0";

    /// <summary>화면에 보여 줄 통계(이름 순서 유지). 나머지는 값이 변했어도 숨긴다.</summary>
    internal static readonly string[] Interesting =
    [
        "session logical reads",
        "db block gets",
        "consistent gets",
        "physical reads",
        "redo size",
        "user commits",
        "parse count (total)",
        "parse count (hard)",
        "execute count",
        "sorts (memory)",
        "sorts (disk)",
        "bytes sent via SQL*Net to client",
        "bytes received via SQL*Net from client",
        "SQL*Net roundtrips to/from client",
    ];

    internal static SessionStatisticsSource Source { get; } = new(Query, Interesting);
}
