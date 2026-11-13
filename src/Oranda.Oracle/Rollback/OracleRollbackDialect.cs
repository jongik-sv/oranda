using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Literals;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Rollback;

/// <summary>
/// Oracle 되돌리기 방언(<see cref="IRollbackDialect"/>, 설계 6.6·7.1·15장). 값 읽기 식·리터럴·값 종류는 Oracle 계열 공용 규칙
/// (<see cref="OracleFamilyLiterals"/>, RB-01)을 그대로 쓴다. 시점 읽기 질의는 데이터 복사와 같은 순서·같은 글이다(<see cref="OracleCopyDialect.Point"/>).
/// Tibero 방언(RB-11)은 이 클래스와 같은 모양으로 두고 실측(RB-13)이 다른 값만 바꾼다.
/// 공급자(통로·실행 서비스·세션 훅)는 <see cref="Instance"/> 를 직접 넘기고, 앱(시점 비교 화면 3 런처 등)은 DBMS 키 등록(<c>Dbms = Oracle</c>)을
/// 선택기로 꺼낸다(디스패처 없음). 키 등록이 없으면 실제 앱에서 「이 DBMS 에서는 시점 비교를 쓸 수 없습니다」로 막힌다(fbfix-1113 FX-01).
/// 상태가 없는 클래스라 선택기가 만든 하나와 <see cref="Instance"/> 는 같은 일을 한다.
/// </summary>
[OrandaService(typeof(IRollbackDialect), Dbms = Dbms.Oracle)]
internal sealed class OracleRollbackDialect : IRollbackDialect
{
    public static readonly OracleRollbackDialect Instance = new();

    /// <summary>내부 저장점(RD-5). 사용자가 쓰지 않을 이름이고 문장마다 같은 이름으로 옮긴다.</summary>
    public const string InternalSavepoint = "ORANDA$RB";

    /// <summary>11.2·12.1 의 식별자 한도(바이트).</summary>
    public const int ShortIdentifierBytes = 30;

    /// <summary>12.2 위의 식별자 한도(바이트).</summary>
    public const int LongIdentifierBytes = 128;

    private static readonly IReadOnlyList<string> Points = [.. OracleCopyDialect.Point.Queries.Select(q => q.Sql)];

    private static readonly Lazy<string> SideEffects = new(() => EmbeddedCatalog.Text("rollback.side_effects"));

    public Dbms Dbms => Dbms.Oracle;

    public string PointName => OracleCopyDialect.Point.Name;

    /// <summary><c>DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER</c> → <c>TIMESTAMP_TO_SCN(SYSTIMESTAMP)</c>(설계 7.1). <c>V$DATABASE</c> 는 권한이 필요해 쓰지 않는다.</summary>
    public IReadOnlyList<string> PointQueries => Points;

    public string SavepointName => InternalSavepoint;

    /// <summary>ODP.NET 의 <c>RETURNING ROWID INTO :ORANDA_RID</c> 출력 바인드. 동작은 RB-12 통합 시험이 확인한다.</summary>
    public bool SupportsReturningRowId => true;

    public string? SideEffectsSql => SideEffects.Value;

    public string AsOf(long point) => "AS OF SCN " + point.ToString(CultureInfo.InvariantCulture);

    public RollbackValueKind KindOf(ColumnInfo column) => OracleFamilyLiterals.KindOf(column);

    public string ReadExpression(RollbackColumn column, string qualifiedColumn) => OracleFamilyLiterals.ReadExpression(column, qualifiedColumn);

    public string? Literal(RollbackColumn column, object? value, out string? reason) => OracleFamilyLiterals.Literal(column, value, out reason);

    public string RowIdLiteral(string rowId) => OracleFamilyLiterals.RowIdLiteral(rowId);

    /// <summary>
    /// 12.2 부터 긴 식별자(128바이트)다. 버전을 읽지 못하면 짧은 한도(30)로 본다(백업 테이블 이름을 더 짧게 자를 뿐 실패하지 않는다).
    /// 12.2 위라도 <c>COMPATIBLE</c> 이 12.2 아래면 서버가 30바이트로 거부할 수 있다(드묾, 그때는 CTAS 실패 문구가 알린다).
    /// </summary>
    public int MaxIdentifierBytes(ServerInfo server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var parts = server.Version.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return major > 12 || (major == 12 && minor >= 2) ? LongIdentifierBytes : ShortIdentifierBytes;
    }
}
