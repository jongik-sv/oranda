using Oranda.Core.Connection;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>
/// <see cref="ITableEditDialect"/> 기본 구현(Oracle 규칙). 방언은 기본 멤버를 그대로 쓰거나, 다른 곳만 이 함수를 감싸 고친다.
/// </summary>
public static class TableEditDefaults
{
    /// <summary>Oracle 서버 이름: <c>SYS_</c>(SYS_C…, SYS_IL…)·<c>BIN$</c>(휴지통).</summary>
    public static bool IsServerName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("SYS_", StringComparison.Ordinal) || name.StartsWith("BIN$", StringComparison.Ordinal);
    }

    /// <summary>
    /// 테이블 압축 절(<see cref="ITableEditDialect.CompressionClause"/>). Oracle 12c 이상 <c>ROW STORE COMPRESS …</c>, 11g·모름 <c>COMPRESS BASIC</c>·<c>COMPRESS FOR OLTP</c>(12c 도 받는다),
    /// Tibero <c>COMPRESS</c>(실측 12).
    /// </summary>
    public static string CompressionClause(TableCompression compression, ServerInfo? server)
    {
        if (server is { Dbms: Dbms.Tibero })
        {
            return compression == TableCompression.None ? "NOCOMPRESS" : "COMPRESS";
        }

        var rowStore = server is { Dbms: Dbms.Oracle, MajorVersion: >= 12 };
        return compression switch
        {
            TableCompression.None => "NOCOMPRESS",
            TableCompression.Basic => rowStore ? "ROW STORE COMPRESS BASIC" : "COMPRESS BASIC",
            TableCompression.Advanced => rowStore ? "ROW STORE COMPRESS ADVANCED" : "COMPRESS FOR OLTP",
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "읽기만 하는 압축은 문장에 쓰지 않는다."),
        };
    }

    /// <summary>저장 페이지 기능의 Core 판정 이유 키 머리.</summary>
    public const string StorageReasonPrefix = "tableEdit.unsupported.";

    /// <summary>
    /// 저장 페이지(설계 18.2) 기능의 Core 판정 — 방언 판정(<see cref="ITableEditDialect.UnsupportedReasonKey"/>)과 함께 본다(둘 중 하나라도 이유가 있으면 안 됨).
    /// 방언이 아직 이 기능 값을 모르는 것(「된다」로 답함)에 대비해 보수적으로: 서버를 모르면 끔, Tibero 끔(실측 12·13),
    /// <see cref="TableEditFeature.OnlineMove"/> = Oracle 12.2+ Enterprise, <see cref="TableEditFeature.MoveUpdateIndexes"/> = Oracle 12.2+,
    /// <see cref="TableEditFeature.SecureFileLob"/> = Oracle 11+, <see cref="TableEditFeature.AdvancedRowCompression"/> = Oracle Enterprise,
    /// <see cref="TableEditFeature.BasicTableCompression"/> = Oracle Enterprise·Tibero. 다른 기능은 null.
    /// </summary>
    public static string? StorageReasonKey(TableEditFeature feature, ServerInfo? server)
    {
        var oracle = server is { Dbms: Dbms.Oracle };
        var enterprise = server?.Edition is not ("Standard" or "Express");
        var supported = feature switch
        {
            TableEditFeature.OnlineMove => oracle && AtLeast(server!, 12, 2) && enterprise,
            TableEditFeature.MoveUpdateIndexes => oracle && AtLeast(server!, 12, 2),
            TableEditFeature.SecureFileLob => oracle && server!.MajorVersion >= 11,
            TableEditFeature.AdvancedRowCompression => oracle && enterprise,
            TableEditFeature.BasicTableCompression => server is { Dbms: Dbms.Tibero } || (oracle && enterprise),
            _ => true,
        };
        if (supported)
        {
            return null;
        }

        return StorageReasonPrefix + feature switch
        {
            TableEditFeature.OnlineMove => "onlineMove",
            TableEditFeature.MoveUpdateIndexes => "moveUpdateIndexes",
            TableEditFeature.SecureFileLob => "secureFileLob",
            TableEditFeature.BasicTableCompression => "basicTableCompression",
            _ => "advancedRowCompression",
        };
    }

    private static bool AtLeast(ServerInfo server, int major, int minor)
    {
        var parts = server.Version.Split('.');
        var minorPart = parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
        return server.MajorVersion > major || (server.MajorVersion == major && minorPart >= minor);
    }

    /// <summary>LOB 저장 절을 쓸 수 있는 형식(CLOB·NCLOB·BLOB). BFILE·LONG 은 아님.</summary>
    public static bool IsLobType(string? dataType) =>
        dataType?.Trim().ToUpperInvariant() is "CLOB" or "NCLOB" or "BLOB";

    /// <summary>Oracle SQL 예약어(<c>V$RESERVED_WORDS</c> 의 RESERVED = 'Y', SQL Language Reference 「Oracle SQL Reserved Words」).</summary>
    public static bool IsReservedWord(string upperName) => ReservedWords.Contains(upperName);

    /// <summary>
    /// 비교용 형식 표기(설계 8.4): 대소문자·공백, <c>NUMBER</c> 의 정밀도 없음/눈금 0(<c>NUMBER(*,0)</c>)·INTEGER, <c>NUMBER(p,0)</c> = <c>NUMBER(p)</c>,
    /// VARCHAR → VARCHAR2(Tibero 사전), CHAR 기본 길이 1, TIMESTAMP 기본 소수 6, FLOAT 기본 126, 길이가 없는 형식의 길이 값은 무시. 길이 기준은 모델 값 그대로.
    /// </summary>
    public static string NormalizeType(ColumnDraft column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return ColumnTypeText.Canonical(column);
    }

    /// <summary>비교용 기본값 표기: 앞뒤 공백·줄바꿈을 떼고, 빈 값과 글자 <c>NULL</c>(<c>DEFAULT NULL</c> 뒤 서버 값)은 null, 나머지는 식 규칙(<see cref="NormalizeExpression"/>).</summary>
    public static string? NormalizeDefault(string? serverText)
    {
        var canonical = TableEditExpression.Canonical(serverText);
        return canonical is null or "NULL" ? null : canonical;
    }

    /// <summary>
    /// 비교용 식 표기: 서버가 붙인 따옴표(대문자 일반 이름만)를 벗기고, 따옴표·리터럴 밖은 대문자, 공백은 단어 사이 하나, 바깥을 다 감싼 괄호는 벗김. 빈 식은 null.
    /// </summary>
    public static string? NormalizeExpression(string? serverText) => TableEditExpression.Canonical(serverText);

    /// <summary>비교용 CHECK 조건 표기(식 규칙과 같음). 리터럴 안 대소문자는 가린다.</summary>
    public static string? NormalizeCondition(string? serverText) => TableEditExpression.Canonical(serverText);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER", "COLUMN",
        "COLUMN_VALUE", "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT",
        "DROP", "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IMMEDIATE", "IN",
        "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS",
        "MINUS", "MLSLABEL", "MODE", "MODIFY", "NESTED_TABLE_ID", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF",
        "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW",
        "ROWID", "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE",
        "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2", "VIEW",
        "WHENEVER", "WHERE", "WITH",
    };
}
