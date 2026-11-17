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
