using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Rollback.Capture;

/// <summary>원복 문장·내부 질의 글의 식별자 도우미. 사람이 읽기 쉽게 일반 대문자 이름은 따옴표 없이 쓴다(설계 6.7 예).</summary>
public static class RollbackSql
{
    /// <summary>
    /// 반대 문장(RD-8)을 만들면 값을 잃는 원복 문장의 주석: 넣은 행의 큰 열에 값이 있어 조건·리터럴로 담지 못한 경우.
    /// 엔진이 붙이고, 반대 문장 빌더(<c>Script/RollbackReverse</c>)는 이 주석이 붙은 문장의 반대 문장을 만들지 않는다.
    /// </summary>
    public const string NoReverseNote = "큰 열에 값이 있는 행입니다. 원복 실행을 다시 되돌리는 반대 문장은 만들지 않습니다";

    /// <summary>Oracle 예약어(<c>V$RESERVED_WORDS.RESERVED = 'Y'</c> 와 SQL 예약어). 열 이름이 이것이면 큰따옴표로 감싼다.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY", "CHAR", "CHECK", "CLUSTER", "COLUMN",
        "COMMENT", "COMPRESS", "CONNECT", "CREATE", "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP",
        "ELSE", "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING", "IDENTIFIED", "IMMEDIATE", "IN",
        "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER", "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS",
        "MINUS", "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL", "NUMBER", "OF", "OFFLINE", "ON",
        "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR", "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID",
        "ROWNUM", "ROWS", "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL", "SYNONYM", "SYSDATE",
        "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE", "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2",
        "VIEW", "WHENEVER", "WHERE", "WITH",
    };

    /// <summary>식별자 하나(일반 대문자 이름이고 예약어가 아니면 그대로, 아니면 큰따옴표).</summary>
    public static string Ident(string name) => SqlIdentifier.Quote(name, Reserved.Contains);

    /// <summary><c>소유자.이름</c>.</summary>
    public static string Table(DbObjectRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Ident(table.Owner) + "." + Ident(table.Name);
    }
}
