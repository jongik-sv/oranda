using System.Globalization;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.Oracle.Schema;

/// <summary>
/// 테이블 편집기 Oracle 방언(설계 8.1·18.8, 계획 Task 3). 정규화·서버 이름·예약어는 <see cref="ITableEditDialect"/> 기본 멤버(Oracle 규칙)를 그대로 쓴다.
/// 판단 근거 = 실측 <c>docs/design/oracle-table-edit-probe.md</c>(26ai 23.26 Free · 11g XE 11.2).
/// </summary>
[OrandaService(typeof(ITableEditDialect), Dbms = Dbms.Oracle)]
internal sealed class OracleTableEditDialect : ITableEditDialect
{
    /// <summary>12.1 미만 서버(신원·숨김 열 문법 없음, 실측 18.5).</summary>
    internal const string Needs12cKey = "TableEdit.unsupported.oracle12c";

    /// <summary>12.2 미만 서버(일반 → 파티션 테이블 바꾸기, TE-41).</summary>
    internal const string Needs122Key = "TableEdit.unsupported.oracle122";

    /// <summary>23ai 미만 서버(도메인).</summary>
    internal const string Needs23Key = "TableEdit.unsupported.oracle23";

    /// <summary>Standard·Express Edition(비트맵 인덱스·파티션 ORA-00439, 실측 18.4·18.6).</summary>
    internal const string NeedsEnterpriseKey = "TableEdit.unsupported.oracleEnterprise";

    /// <summary>서버를 모름(버전·에디션에 따라 갈리는 기능은 보수적으로 끈다).</summary>
    internal const string UnknownServerKey = "TableEdit.unsupported.unknownServer";

    private static readonly string[] Types =
    [
        "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "NUMBER", "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "CLOB", "NCLOB", "BLOB", "RAW",
    ];

    public Dbms Dbms => Dbms.Oracle;

    public IReadOnlyList<string> TypeNames => Types;

    /// <summary>실측 11·5a: 가상 식 <c>MODIFY (c AS (expr))</c> 는 11g·26ai 모두 된다(데이터가 있어도).</summary>
    public bool SupportsVirtualColumnModify => true;

    /// <summary>Oracle 은 휴지통(<c>RECYCLEBIN</c>)이 기본으로 켜져 있다 — DROP TABLE 은 되살릴 수 있지만 열·제약 DROP 은 원래 되돌릴 수 없다.</summary>
    public bool PurgesOnDrop => false;

    /// <summary>12.2 이상 128 바이트, 그 밖·모름 30 바이트(실측 11: 11g 31자 ORA-00972).</summary>
    public int MaxIdentifierBytes(ServerInfo? server) => server is not null && AtLeast(server, 12, 2) ? 128 : 30;

    public string? UnsupportedReasonKey(TableEditFeature feature, ServerInfo? server) => feature switch
    {
        TableEditFeature.IdentityColumn or TableEditFeature.InvisibleColumn => Requires(server, 12, 1, Needs12cKey, enterprise: false),
        TableEditFeature.DomainColumn => Requires(server, 23, 0, Needs23Key, enterprise: false),
        TableEditFeature.BitmapIndex or TableEditFeature.Partitioning => Requires(server, 0, 0, null, enterprise: true),
        TableEditFeature.PartitionConversion => Requires(server, 12, 2, Needs122Key, enterprise: true),
        _ => null,
    };

    /// <summary>버전·에디션 조건. 서버를 모르면 <see cref="UnknownServerKey"/>. 에디션을 모르면(배너에서 못 뽑음) 막지 않는다 — 서버가 ORA-00439 로 알린다.</summary>
    private static string? Requires(ServerInfo? server, int major, int minor, string? versionKey, bool enterprise)
    {
        if (server is null)
        {
            return UnknownServerKey;
        }

        if (versionKey is not null && !AtLeast(server, major, minor))
        {
            return versionKey;
        }

        return enterprise && server.Edition is "Standard" or "Express" ? NeedsEnterpriseKey : null;
    }

    /// <summary>서버 버전이 <paramref name="major"/>.<paramref name="minor"/> 이상인가(<c>23.26.3.0.0</c> 꼴).</summary>
    internal static bool AtLeast(ServerInfo server, int major, int minor)
    {
        var parts = server.Version.Split('.');
        var serverMajor = server.MajorVersion;
        var serverMinor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        return serverMajor > major || (serverMajor == major && serverMinor >= minor);
    }
}
