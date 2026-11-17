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

    /// <summary>Oracle SQL 예약어(<c>V$RESERVED_WORDS</c> 의 RESERVED = 'Y', SQL Language Reference 「Oracle SQL Reserved Words」).</summary>
    public static bool IsReservedWord(string upperName) => ReservedWords.Contains(upperName);

    /// <summary>비교용 형식 표기(설계 8.4).</summary>
    public static string NormalizeType(ColumnDraft column) => throw new NotImplementedException("T1b");

    /// <summary>비교용 기본값 표기(설계 8.4).</summary>
    public static string? NormalizeDefault(string? serverText) => throw new NotImplementedException("T1b");

    /// <summary>비교용 식 표기(설계 8.4).</summary>
    public static string? NormalizeExpression(string? serverText) => throw new NotImplementedException("T1b");

    /// <summary>비교용 CHECK 조건 표기(설계 8.4).</summary>
    public static string? NormalizeCondition(string? serverText) => throw new NotImplementedException("T1b");

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
