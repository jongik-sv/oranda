namespace Oranda.Language.Snippets;

/// <summary>
/// 내장 항목의 영어 이름·설명(영어 화면용). 한국어 원문은 <see cref="SnippetLibrary"/> 의 목록이 정본이고,
/// 여기에는 종류+줄임말 → 영어 글만 둔다. 이름은 한국어가 섞인 것만 적는다(나머지는 원래 이름 그대로).
/// 사용자 조각은 번역하지 않는다.
/// </summary>
public static partial class SnippetLibrary
{
    private static readonly Dictionary<(SnippetKind Kind, string Prefix), (string? Name, string Description)> English = BuildEnglish();

    /// <summary>화면 언어(<c>ko</c> | <c>en</c>)의 이름. 영어 글이 없으면 원래 이름.</summary>
    public static string DisplayName(Snippet snippet, string? language)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return IsEnglish(language) && EnglishOf(snippet) is { Name: { } name } ? name : snippet.Name;
    }

    /// <summary>화면 언어의 한 줄 설명. 영어 글이 없으면 원래 설명.</summary>
    public static string DisplayDescription(Snippet snippet, string? language)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return IsEnglish(language) && EnglishOf(snippet) is { } en ? en.Description : snippet.Description;
    }

    /// <summary>
    /// 목록 행에 줄임말과 나란히 보일 짧은 글. 설명이 있으면 설명, 없으면 이름(줄임말과 다를 때만), 그것도 아니면 본문 첫 줄.
    /// 「ROWID ROWID」 처럼 줄임말을 두 번 보이지 않게 한다.
    /// </summary>
    public static string Summary(Snippet snippet, string? language)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        var description = DisplayDescription(snippet, language).Trim();
        if (description.Length > 0)
        {
            return description;
        }

        var name = DisplayName(snippet, language).Trim();
        if (name.Length > 0 && !string.Equals(name, snippet.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return snippet.Example.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
    }

    /// <summary>영어 글이 있는 내장 항목인가(시험용).</summary>
    public static bool HasEnglish(Snippet snippet) => EnglishOf(snippet) is not null;

    private static bool IsEnglish(string? language) => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

    private static (string? Name, string Description)? EnglishOf(Snippet snippet) =>
        !snippet.IsUser && English.TryGetValue((snippet.Kind, snippet.Prefix), out var en) ? en : null;

    private static Dictionary<(SnippetKind, string), (string?, string)> BuildEnglish()
    {
        var map = new Dictionary<(SnippetKind, string), (string?, string)>();

        void Add(SnippetKind kind, string prefix, string description, string? name = null) => map[(kind, prefix)] = (name, description);

        // 조각
        Add(SnippetKind.Snippet, "sel", "Basic query");
        Add(SnippetKind.Snippet, "selc", "Count rows");
        Add(SnippetKind.Snippet, "selj", "Join two tables");
        Add(SnippetKind.Snippet, "selg", "Group and aggregate");
        Add(SnippetKind.Snippet, "selr", "Top N rows with FETCH FIRST", "SELECT top N rows");
        Add(SnippetKind.Snippet, "ins", "Insert one row");
        Add(SnippetKind.Snippet, "inss", "Insert query results");
        Add(SnippetKind.Snippet, "upd", "Update rows");
        Add(SnippetKind.Snippet, "del", "Delete rows");
        Add(SnippetKind.Snippet, "mrg", "Update if present, insert if not");
        Add(SnippetKind.Snippet, "with", "Common table expression");
        Add(SnippetKind.Snippet, "case", "Conditional branch");
        Add(SnippetKind.Snippet, "ct", "Create a table");
        Add(SnippetKind.Snippet, "cidx", "Create an index");
        Add(SnippetKind.Snippet, "cv", "Create a view");
        Add(SnippetKind.Snippet, "cseq", "Create a sequence");
        Add(SnippetKind.Snippet, "alt", "Add a column");
        Add(SnippetKind.Snippet, "expl", "Estimated execution plan");
        Add(SnippetKind.Snippet, "anon", "PL/SQL anonymous block", "Anonymous block");
        Add(SnippetKind.Snippet, "proc", "Procedure");
        Add(SnippetKind.Snippet, "func", "Function");
        Add(SnippetKind.Snippet, "cur", "Loop over query results", "FOR loop");
        Add(SnippetKind.Snippet, "ifb", "IF statement");
        Add(SnippetKind.Snippet, "exc", "Exception handling");
        Add(SnippetKind.Snippet, "trg", "Trigger");

        // 옵티마이저 힌트
        Add(SnippetKind.Hint, "FULL", "Reads the whole table without using an index.");
        Add(SnippetKind.Hint, "INDEX", "Reads through the given index. Without an index name the optimizer picks one.");
        Add(SnippetKind.Hint, "INDEX_DESC", "Reads the index in descending order. Useful for the latest rows.");
        Add(SnippetKind.Hint, "INDEX_FFS", "Reads the whole index quickly without touching the table.");
        Add(SnippetKind.Hint, "NO_INDEX", "Keeps the given index from being used.");
        Add(SnippetKind.Hint, "LEADING", "Sets the tables that come first in the join order.");
        Add(SnippetKind.Hint, "ORDERED", "Joins in the order the tables appear in the FROM clause.");
        Add(SnippetKind.Hint, "USE_NL", "Joins the table with nested loops.");
        Add(SnippetKind.Hint, "USE_HASH", "Joins the table with a hash join.");
        Add(SnippetKind.Hint, "USE_MERGE", "Joins the table with a sort-merge join.");
        Add(SnippetKind.Hint, "PARALLEL", "Reads in parallel. Meant for large queries and uses more server resources.");
        Add(SnippetKind.Hint, "NO_PARALLEL", "Prevents parallel execution.");
        Add(SnippetKind.Hint, "APPEND", "Uses direct-path INSERT. Fast for bulk loads, but the table cannot be read again before commit.");
        Add(SnippetKind.Hint, "FIRST_ROWS", "Picks a plan that returns the first n rows quickly.");
        Add(SnippetKind.Hint, "ALL_ROWS", "Picks the plan with the best overall throughput.");
        Add(SnippetKind.Hint, "GATHER_PLAN_STATISTICS", "Collects actual row counts while running so DBMS_XPLAN.DISPLAY_CURSOR can show them with ALLSTATS.");
        Add(SnippetKind.Hint, "MATERIALIZE", "Stores the WITH clause result in a temporary table for reuse.");
        Add(SnippetKind.Hint, "INLINE", "Expands the WITH clause into the query without a temporary table.");
        Add(SnippetKind.Hint, "RESULT_CACHE", "Keeps the result in the server result cache.");
        Add(SnippetKind.Hint, "DRIVING_SITE", "Runs a database link join on the side where the table lives.");

        // 내장 함수
        Add(SnippetKind.Function, "NVL", "Returns the replacement when expr is NULL.", "NVL(expr, replacement)");
        Add(SnippetKind.Function, "NVL2", "value1 when expr is not NULL, value2 when it is.", "NVL2(expr, value1, value2)");
        Add(SnippetKind.Function, "COALESCE", "Returns the first non-NULL value.");
        Add(SnippetKind.Function, "NULLIF", "NULL when the two values are equal, otherwise a.");
        Add(SnippetKind.Function, "DECODE", "Compares values and branches (Oracle only).", "DECODE(expr, search1, result1, ..., default)");
        Add(SnippetKind.Function, "TO_CHAR", "Converts a number or date to a string.", "TO_CHAR(value [, format])");
        Add(SnippetKind.Function, "TO_DATE", "Converts a string to a date.", "TO_DATE(string [, format])");
        Add(SnippetKind.Function, "TO_NUMBER", "Converts a string to a number.", "TO_NUMBER(string [, format])");
        Add(SnippetKind.Function, "TO_TIMESTAMP", "Converts a string to a TIMESTAMP.", "TO_TIMESTAMP(string [, format])");
        Add(SnippetKind.Function, "TRUNC", "Truncates a date to a unit (DD, MM, YYYY and so on) or a number to decimal places.", "TRUNC(value [, unit])");
        Add(SnippetKind.Function, "ROUND", "Rounds a value. Works on dates too.", "ROUND(value [, places])");
        Add(SnippetKind.Function, "ADD_MONTHS", "The date n months later (or earlier).", "ADD_MONTHS(date, n)");
        Add(SnippetKind.Function, "MONTHS_BETWEEN", "Number of months between two dates.", "MONTHS_BETWEEN(date1, date2)");
        Add(SnippetKind.Function, "LAST_DAY", "Last day of the month.", "LAST_DAY(date)");
        Add(SnippetKind.Function, "SUBSTR", "Substring. A negative start counts from the end.", "SUBSTR(string, start [, length])");
        Add(SnippetKind.Function, "INSTR", "Position of the search string (0 when not found).", "INSTR(string, search [, start [, nth]])");
        Add(SnippetKind.Function, "REPLACE", "Replaces text in a string.", "REPLACE(string, search [, replacement])");
        Add(SnippetKind.Function, "TRIM", "Removes spaces or the given character from both ends (or one end).", "TRIM([LEADING|TRAILING|BOTH] [char FROM] value)");
        Add(SnippetKind.Function, "LPAD", "Pads on the left to the given length.", "LPAD(string, length [, pad])");
        Add(SnippetKind.Function, "RPAD", "Pads on the right to the given length.", "RPAD(string, length [, pad])");
        Add(SnippetKind.Function, "LENGTH", "Number of characters.", "LENGTH(string)");
        Add(SnippetKind.Function, "UPPER", "Converts to uppercase.", "UPPER(string)");
        Add(SnippetKind.Function, "LOWER", "Converts to lowercase.", "LOWER(string)");
        Add(SnippetKind.Function, "REGEXP_LIKE", "True when the string matches the regular expression (in WHERE).", "REGEXP_LIKE(string, pattern [, options])");
        Add(SnippetKind.Function, "REGEXP_SUBSTR", "Substring that matches the regular expression.", "REGEXP_SUBSTR(string, pattern [, start [, nth [, options [, group]]]])");
        Add(SnippetKind.Function, "REGEXP_REPLACE", "Replaces text using a regular expression.", "REGEXP_REPLACE(string, pattern [, replacement ...])");
        Add(SnippetKind.Function, "LISTAGG", "Joins the values of a group with a delimiter.", "LISTAGG(expr [, delimiter]) WITHIN GROUP (ORDER BY ...)");
        Add(SnippetKind.Function, "COUNT", "Row count. COUNT(expr) does not count NULLs.", "COUNT(* | [DISTINCT] expr)");
        Add(SnippetKind.Function, "SUM", "Sum.", "SUM(expr)");
        Add(SnippetKind.Function, "AVG", "Average (NULLs excluded).", "AVG(expr)");
        Add(SnippetKind.Function, "MIN", "Minimum.", "MIN(expr)");
        Add(SnippetKind.Function, "MAX", "Maximum.", "MAX(expr)");
        Add(SnippetKind.Function, "ROW_NUMBER", "Sequence number within a group (ties get different numbers).");
        Add(SnippetKind.Function, "RANK", "Rank (ties share a rank and the next number is skipped).");
        Add(SnippetKind.Function, "DENSE_RANK", "Rank (ties share a rank and no number is skipped).");
        Add(SnippetKind.Function, "LAG", "Value from a previous row.", "LAG(expr [, offset [, default]]) OVER (...)");
        Add(SnippetKind.Function, "LEAD", "Value from a following row.", "LEAD(expr [, offset [, default]]) OVER (...)");
        Add(SnippetKind.Function, "SYS_GUID", "Unique identifier (RAW 16).");
        Add(SnippetKind.Function, "CAST", "Converts the data type.", "CAST(expr AS type)");
        Add(SnippetKind.Function, "EXTRACT", "Part of a date as a number.", "EXTRACT(YEAR|MONTH|DAY|HOUR FROM date)");
        Add(SnippetKind.Function, "JSON_VALUE", "Extracts one value from a JSON document.", "JSON_VALUE(document, '$.path')");

        // 의사 컬럼
        Add(SnippetKind.PseudoColumn, "ROWNUM", "Number given to rows in the order they are fetched (from 1). Assigned before ORDER BY.");
        Add(SnippetKind.PseudoColumn, "ROWID", "Physical address of a row. The fastest way to reach a row, but it can change when the row moves.");
        Add(SnippetKind.PseudoColumn, "LEVEL", "Depth in a hierarchical query (CONNECT BY). The root is 1.");
        Add(SnippetKind.PseudoColumn, "SYSDATE", "Current date and time of the database server (DATE).");
        Add(SnippetKind.PseudoColumn, "SYSTIMESTAMP", "Current time of the database server with time zone and fractional seconds.");
        Add(SnippetKind.PseudoColumn, "CURRVAL", "Current value of the sequence (call NEXTVAL first in the same session).", "sequence.CURRVAL");
        Add(SnippetKind.PseudoColumn, "NEXTVAL", "Next value of the sequence.", "sequence.NEXTVAL");
        Add(SnippetKind.PseudoColumn, "ORA_ROWSCN", "SCN of the last change to the row (approximate).");
        Add(SnippetKind.PseudoColumn, "CONNECT_BY_ISLEAF", "1 when the row is a leaf in a hierarchical query.");
        Add(SnippetKind.PseudoColumn, "CONNECT_BY_ISCYCLE", "1 when the hierarchical query has a cycle (with NOCYCLE).");
        Add(SnippetKind.PseudoColumn, "COLUMN_VALUE", "Name of the value column of a collection unnested with TABLE().");
        Add(SnippetKind.PseudoColumn, "USER", "Name of the connected user.");
        Add(SnippetKind.PseudoColumn, "UID", "Number of the current user.");
        return map;
    }
}
