namespace Oranda.Language.Completion;

/// <summary>DB 없이 내는 후보: 키워드·내장 함수·옵티마이저 힌트 이름.</summary>
internal static class CompletionWords
{
    /// <summary>문장 첫머리 키워드(SQL·PL/SQL·SQL*Plus 일부).</summary>
    public static readonly string[] StatementStart =
    [
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "CREATE", "ALTER", "DROP", "TRUNCATE", "COMMENT",
        "GRANT", "REVOKE", "COMMIT", "ROLLBACK", "SAVEPOINT", "BEGIN", "DECLARE", "EXPLAIN", "CALL", "EXEC", "DESC",
        "LOCK", "RENAME", "PURGE", "FLASHBACK", "SET", "SHOW",
    ];

    /// <summary>문장 중간 키워드.</summary>
    public static readonly string[] Clause =
    [
        "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "LIKE", "BETWEEN", "IS", "NULL", "AS", "ON", "USING",
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "NATURAL", "GROUP BY", "ORDER BY", "HAVING",
        "UNION", "UNION ALL", "INTERSECT", "MINUS", "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END", "ASC", "DESC",
        "NULLS FIRST", "NULLS LAST", "FETCH FIRST", "ROWS ONLY", "OFFSET", "CONNECT BY", "START WITH", "PRIOR",
        "PARTITION BY", "OVER", "INTO", "VALUES", "SET", "RETURNING", "FOR UPDATE", "NOWAIT", "SKIP LOCKED",
        "ROWNUM", "ROWID", "SYSDATE", "SYSTIMESTAMP", "USER", "LEVEL", "DUAL",
        "LOOP", "IF", "ELSIF", "EXCEPTION", "RETURN", "EXIT", "WHILE", "FOR", "OPEN", "FETCH", "CLOSE", "RAISE",
    ];

    /// <summary>자주 쓰는 내장 함수.</summary>
    public static readonly string[] Functions =
    [
        "NVL", "NVL2", "COALESCE", "DECODE", "NULLIF", "TO_CHAR", "TO_DATE", "TO_NUMBER", "TO_TIMESTAMP", "CAST",
        "SUBSTR", "INSTR", "LENGTH", "UPPER", "LOWER", "INITCAP", "TRIM", "LTRIM", "RTRIM", "LPAD", "RPAD", "REPLACE",
        "REGEXP_LIKE", "REGEXP_SUBSTR", "REGEXP_REPLACE", "REGEXP_INSTR", "CONCAT", "ROUND", "TRUNC", "MOD", "ABS",
        "CEIL", "FLOOR", "GREATEST", "LEAST", "COUNT", "SUM", "AVG", "MIN", "MAX", "LISTAGG", "ROW_NUMBER", "RANK",
        "DENSE_RANK", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "ADD_MONTHS", "MONTHS_BETWEEN", "LAST_DAY",
        "NEXT_DAY", "EXTRACT", "SYS_GUID", "JSON_VALUE", "JSON_QUERY", "JSON_OBJECT", "JSON_ARRAYAGG", "XMLAGG",
    ];

    /// <summary>옵티마이저 힌트 이름(<c>/*+ … */</c>).</summary>
    public static readonly string[] Hints =
    [
        "ALL_ROWS", "FIRST_ROWS", "INDEX", "NO_INDEX", "INDEX_ASC", "INDEX_DESC", "INDEX_FFS", "INDEX_SS",
        "INDEX_COMBINE", "INDEX_JOIN", "FULL", "PARALLEL", "NO_PARALLEL", "PARALLEL_INDEX", "PQ_DISTRIBUTE",
        "ENABLE_PARALLEL_DML", "USE_NL", "NO_USE_NL", "USE_HASH", "NO_USE_HASH", "USE_MERGE", "NO_USE_MERGE",
        "LEADING", "ORDERED", "SWAP_JOIN_INPUTS", "NO_SWAP_JOIN_INPUTS", "APPEND", "APPEND_VALUES", "NOAPPEND",
        "DRIVING_SITE", "MATERIALIZE", "INLINE", "MERGE", "NO_MERGE", "PUSH_PRED", "NO_PUSH_PRED", "PUSH_SUBQ",
        "NO_PUSH_SUBQ", "UNNEST", "NO_UNNEST", "USE_CONCAT", "NO_EXPAND", "REWRITE", "NO_REWRITE",
        "STAR_TRANSFORMATION", "FACT", "NO_FACT", "CARDINALITY", "DYNAMIC_SAMPLING", "OPT_PARAM", "QB_NAME",
        "GATHER_PLAN_STATISTICS", "MONITOR", "NO_MONITOR", "RESULT_CACHE", "NO_RESULT_CACHE",
        "IGNORE_ROW_ON_DUPKEY_INDEX", "CURSOR_SHARING_EXACT", "NATIVE_FULL_OUTER_JOIN", "NO_QUERY_TRANSFORMATION",
        "OPTIMIZER_FEATURES_ENABLE", "USE_BAND", "USE_CUBE", "VECTOR_TRANSFORM", "NO_VECTOR_TRANSFORM",
    ];
}
