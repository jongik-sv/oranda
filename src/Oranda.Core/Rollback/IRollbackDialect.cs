using Oranda.Core.Connection;
using Oranda.Core.Metadata;

namespace Oranda.Core.Rollback;

/// <summary>
/// 공급자 방언(설계 6.6·7.1·15장). Oracle·Tibero 가 각자 구현한다(RB-10·RB-11). Oracle 계열 공통 규칙은 <c>Oranda.Core.Rollback.Literals</c>(RB-01)가 준다.
/// </summary>
public interface IRollbackDialect
{
    Dbms Dbms { get; }

    /// <summary>시점 이름: <c>"SCN"</c> | <c>"TSN"</c>.</summary>
    string PointName { get; }

    /// <summary>시점 읽기 질의. 차례로 시도하고, 질의마다 행 하나·열 하나를 돌려준다(설계 7.1).</summary>
    IReadOnlyList<string> PointQueries { get; }

    /// <summary>내부 저장점 이름(<c>"ORANDA$RB"</c>, RD-5).</summary>
    string SavepointName { get; }

    /// <summary>한 행 INSERT 에 <c>RETURNING ROWID INTO :ORANDA_RID</c> 를 붙일 수 있는가(실측 전 Tibero = false).</summary>
    bool SupportsReturningRowId { get; }

    /// <summary>시점 지정 읽기 절(예: <c>AS OF SCN 48213907</c>).</summary>
    string AsOf(long point);

    RollbackValueKind KindOf(ColumnInfo column);

    /// <summary>잠금 읽기·직후 값 읽기 SELECT 목록에 넣는 읽는 식(설계 6.6 표).</summary>
    /// <param name="column">열</param>
    /// <param name="qualifiedColumn">별칭이나 표 이름을 붙인 열 참조(예: <c>t."UNIT_PRICE"</c>)</param>
    string ReadExpression(RollbackColumn column, string qualifiedColumn);

    /// <summary>값 → 리터럴. NULL 은 <c>"NULL"</c>. 리터럴로 쓸 수 없으면 null(불완전 이유는 <paramref name="reason"/>).</summary>
    /// <param name="column">열</param>
    /// <param name="value"><see cref="ReadExpression"/> 로 읽은 값(대개 고정 형식 글자)</param>
    /// <param name="reason">null 을 돌려줄 때의 이유</param>
    string? Literal(RollbackColumn column, object? value, out string? reason);

    /// <summary>ROWID 글 → 조건 리터럴(<c>CHARTOROWID('…')</c>).</summary>
    string RowIdLiteral(string rowId);

    /// <summary>바인드 <c>:owner</c>, <c>:name</c> → (KIND, NAME) 행을 돌려주는 트리거·CASCADE FK 찾기 질의. null = 찾지 않음.</summary>
    string? SideEffectsSql { get; }

    /// <summary>서버의 식별자 길이 한도(바이트, 11.2·12.1 = 30, 그 위 = 128).</summary>
    int MaxIdentifierBytes(ServerInfo server);
}
