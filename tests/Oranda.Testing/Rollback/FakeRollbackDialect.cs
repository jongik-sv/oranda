using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Literals;

namespace Oranda.Testing.Rollback;

/// <summary>
/// Oracle 규칙의 시험용 방언. 값 읽기 식·리터럴은 Core 공용 도우미(<see cref="OracleFamilyLiterals"/>, 설계 6.6)를 그대로 쓴다.
/// 공급자 방언(RB-10·RB-11)을 기다리지 않고 엔진·스크립트를 시험한다. 시점·저장점·RETURNING 지원 여부는 속성으로 바꿀 수 있다.
/// </summary>
public sealed class FakeRollbackDialect : IRollbackDialect
{
    public Dbms Dbms { get; init; } = Dbms.Oracle;

    public string PointName { get; init; } = "SCN";

    public IReadOnlyList<string> PointQueries { get; init; } =
    [
        "SELECT DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER FROM DUAL",
        "SELECT TIMESTAMP_TO_SCN(SYSTIMESTAMP) FROM DUAL",
    ];

    public string SavepointName { get; init; } = "ORANDA$RB";

    public bool SupportsReturningRowId { get; init; } = true;

    public string? SideEffectsSql { get; init; } =
        "SELECT 'TRIGGER', TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :owner AND TABLE_NAME = :name";

    public int IdentifierBytes { get; init; } = 128;

    public string AsOf(long point) => "AS OF " + PointName + " " + point.ToString(CultureInfo.InvariantCulture);

    public int MaxIdentifierBytes(ServerInfo server) => IdentifierBytes;

    public RollbackValueKind KindOf(ColumnInfo column) => OracleFamilyLiterals.KindOf(column);

    public string ReadExpression(RollbackColumn column, string qualifiedColumn) => OracleFamilyLiterals.ReadExpression(column, qualifiedColumn);

    public string? Literal(RollbackColumn column, object? value, out string? reason) => OracleFamilyLiterals.Literal(column, value, out reason);

    public string RowIdLiteral(string rowId) => OracleFamilyLiterals.RowIdLiteral(rowId);
}
