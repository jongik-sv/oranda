using System.Globalization;
using Oranda.Tibero.Transfer.Copy;

namespace Oranda.Tibero.Rollback;

/// <summary>
/// 되돌리기 지점의 Tibero 방언 값(설계 15장, RB-13 실측 = docs/design/tibero-driver-exec.md 10장). 공유 계약(RB-00) 전에 만든 값 모음이고,
/// <c>TiberoRollbackDialect</c>(<c>IRollbackDialect</c> 구현, RB-11)가 이 값을 그대로 낸다.
/// </summary>
internal static class TiberoRollbackValues
{
    /// <summary>시점 번호 이름(화면·로그 「TSN 48213907」).</summary>
    public const string PointName = "TSN";

    /// <summary>내부 저장점(설계 RD-5). 실측: <c>$</c> 이름 됨, ROLLBACK TO 가 FOR UPDATE 잠금을 푼다.</summary>
    public const string SavepointName = "ORANDA$RB";

    /// <summary>실측: <c>RETURNING ROWID INTO :ORANDA_RID</c> 출력 바인드(Varchar2, 크기 미지정)가 18글자 ROWID 를 준다.</summary>
    public const bool SupportsReturningRowId = true;

    /// <summary>실측(6.2.15): 식별자 128바이트까지 됨, 129 = TBR-23007.</summary>
    public const int MaxIdentifierBytes = 128;

    /// <summary>
    /// 지금 시점을 읽는 질의(차례로 시도, 행 하나·열 하나). 데이터 복사의 같은 시점 읽기와 같은 순서다(<see cref="TiberoCopyDialect.ReadPoint"/>):
    /// 정확한 <c>GET_SYSTEM_CHANGE_NUMBER</c> → 권한 없는 <c>TIMESTAMP_TO_TSN(SYSTIMESTAMP)</c>(몇 초 늦은 TSN, 실측 차이 12) → <c>V$DATABASE.CURRENT_TSN</c>.
    /// </summary>
    public static IReadOnlyList<string> PointQueries { get; } = [.. TiberoCopyDialect.Point.Queries.Select(q => q.Sql)];

    /// <summary>시점 지정 읽기 절(표 참조 뒤). 실측: <c>AS OF TSN n</c>·<c>AS OF SCN n</c> 둘 다 된다. 시점 이름과 맞춰 TSN 을 쓴다.</summary>
    public static string AsOf(long point)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(point);
        return "AS OF TSN " + point.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 표의 부작용(켜진 트리거, 지울 때 자식 행을 지우거나 NULL 로 바꾸는 FK) 찾기. 바인드 <c>:owner</c>·<c>:name</c> → (KIND, NAME) 행.
    /// 실측: <c>ALL_TRIGGERS</c>(TABLE_OWNER·TABLE_NAME·STATUS)·<c>ALL_CONSTRAINTS.DELETE_RULE</c> 가 Oracle 과 같은 사전 이름이다.
    /// </summary>
    public const string SideEffectsSql =
        "SELECT 'TRIGGER' AS KIND, OWNER || '.' || TRIGGER_NAME AS NAME FROM ALL_TRIGGERS"
        + " WHERE TABLE_OWNER = :owner AND TABLE_NAME = :name AND STATUS = 'ENABLED'"
        + " UNION ALL"
        + " SELECT 'FK ' || c.DELETE_RULE AS KIND, c.OWNER || '.' || c.TABLE_NAME || '.' || c.CONSTRAINT_NAME AS NAME"
        + " FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME"
        + " WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND c.DELETE_RULE IN ('CASCADE', 'SET NULL')"
        + " AND p.OWNER = :owner AND p.TABLE_NAME = :name";
}
