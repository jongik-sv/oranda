using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Literals;

namespace Oranda.Tibero.Rollback;

/// <summary>
/// Tibero 되돌리기 방언(설계 6.6·15장). 값은 <see cref="TiberoRollbackValues"/>, 읽는 식·리터럴은 Oracle 계열 공통 규칙(<see cref="OracleFamilyLiterals"/>, RB-01)에
/// 아래 Tibero 차이만 덮어쓴다. RB-13 실측(docs/design/tibero-driver-exec.md 10장)으로 확인했다: TM9·SYYYY·FF9·FF9 TZR·LTZ(UTC)·INTERVAL·RAW·ROWID·UNISTR 가 모두
/// 「읽는 식 → 리터럴 → <c>WHERE c = 리터럴</c> 1행」. 다른 점:
/// <list type="bullet">
/// <item>문자 형식 이름이 <c>VARCHAR</c>·<c>NVARCHAR</c> 로 온다(meta.columns, Oracle <c>VARCHAR2</c>).</item>
/// <item>BINARY_FLOAT 는 <c>TO_BINARY_FLOAT('…')</c> — <c>TO_BINARY_DOUBLE</c> 로 비교하면 0행(실측, 조정자 결정으로 설계 6.6 고침).</item>
/// <item>BINARY_FLOAT·BINARY_DOUBLE 은 서버 글자(<c>TO_CHAR(c)</c>)로 읽는다: 드라이버가 <c>-BINARY_DOUBLE_INFINITY</c> 를 double 로 잘못 읽는다
/// (-2.2E-308, 실측). 글자는 <c>TO_BINARY_FLOAT</c>·<c>TO_BINARY_DOUBLE</c> 로 감싸고 NaN·무한대 글자는 상수로 쓴다.</item>
/// <item>INVISIBLE·IDENTITY 열이 없다(메타데이터가 늘 거짓). 가상 열은 있고 meta.columns 가 알린다.</item>
/// </list>
/// </summary>
[OrandaService(typeof(IRollbackDialect), Dbms = Dbms.Tibero)]
internal sealed class TiberoRollbackDialect : IRollbackDialect
{
    public static TiberoRollbackDialect Instance { get; } = new();

    public Dbms Dbms => Dbms.Tibero;

    public string PointName => TiberoRollbackValues.PointName;

    public IReadOnlyList<string> PointQueries => TiberoRollbackValues.PointQueries;

    public string SavepointName => TiberoRollbackValues.SavepointName;

    public bool SupportsReturningRowId => TiberoRollbackValues.SupportsReturningRowId;

    public string? SideEffectsSql => TiberoRollbackValues.SideEffectsSql;

    public string AsOf(long point) => TiberoRollbackValues.AsOf(point);

    public int MaxIdentifierBytes(ServerInfo server) => TiberoRollbackValues.MaxIdentifierBytes;

    /// <summary>Tibero 사전의 <c>NVARCHAR</c>(Oracle <c>NVARCHAR2</c>)만 덧붙이고 나머지는 공통 규칙.</summary>
    public RollbackValueKind KindOf(ColumnInfo column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var head = column.DataType.Trim().ToUpperInvariant().Split('(', ' ')[0];
        return head == "NVARCHAR" ? RollbackValueKind.NationalText : OracleFamilyLiterals.KindOf(column);
    }

    /// <summary>BINARY_FLOAT·BINARY_DOUBLE 은 서버 글자 <c>TO_CHAR(c)</c>(드라이버의 -무한대 오독을 피한다), 나머지는 공통 규칙.</summary>
    public string ReadExpression(RollbackColumn column, string qualifiedColumn)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedColumn);
        return column.Kind is RollbackValueKind.BinaryFloat or RollbackValueKind.BinaryDouble
            ? $"TO_CHAR({qualifiedColumn})"
            : OracleFamilyLiterals.ReadExpression(column, qualifiedColumn);
    }

    /// <summary><c>TO_CHAR</c> 의 NaN·무한대 글자(실측 <c>Nan</c>·<c>Inf</c>·<c>-Inf</c>)는 상수로, 나머지는 공통 규칙.</summary>
    public string? Literal(RollbackColumn column, object? value, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.Kind is RollbackValueKind.BinaryFloat or RollbackValueKind.BinaryDouble && value is string text && Special(text, column.Kind) is { } constant)
        {
            reason = null;
            return constant;
        }

        return OracleFamilyLiterals.Literal(column, value, out reason);
    }

    public string RowIdLiteral(string rowId) => OracleFamilyLiterals.RowIdLiteral(rowId);

    private static string? Special(string text, RollbackValueKind kind)
    {
        var t = text.Trim();
        var type = kind == RollbackValueKind.BinaryFloat ? "BINARY_FLOAT" : "BINARY_DOUBLE";
        if (t.Equals("Nan", StringComparison.OrdinalIgnoreCase))
        {
            return type + "_NAN";
        }

        return t.ToUpperInvariant() switch
        {
            "INF" or "+INF" => type + "_INFINITY",
            "-INF" => "-" + type + "_INFINITY",
            _ => null,
        };
    }
}
