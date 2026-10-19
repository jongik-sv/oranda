using System.Collections.Frozen;
using Oranda.Core.Formatting;

namespace Oranda.Language.Formatting;

/// <summary>포매터가 쓰는 단어 목록(내장 함수·렉서 밖 키워드·괄호 앞 공백 규칙)과 대소문자 변환. 소유: TSK-05-01.</summary>
internal static class FormatWords
{
    /// <summary>괄호가 뒤따르면 함수 대소문자를 쓰는 내장 함수.</summary>
    public static readonly FrozenSet<string> BuiltinFunctions = new[]
    {
        "ABS","ACOS","ADD_MONTHS","APPROX_COUNT_DISTINCT","ASCII","ASCIISTR","ASIN","ATAN","ATAN2","AVG","BFILENAME","BIN_TO_NUM",
        "BITAND","CARDINALITY","CAST","CEIL","CHR","COALESCE","COLLECT","COMPOSE","CONCAT","CONVERT","CORR","COS","COSH","COUNT",
        "COVAR_POP","COVAR_SAMP","CUME_DIST","CURRENT_DATE","CURRENT_TIMESTAMP","DBTIMEZONE","DECODE","DECOMPOSE","DENSE_RANK",
        "DEPTH","DUMP","EMPTY_BLOB","EMPTY_CLOB","EXP","EXTRACT","EXTRACTVALUE","FIRST_VALUE","FLOOR","FROM_TZ","GREATEST",
        "GROUPING","GROUPING_ID","GROUP_ID","HEXTORAW","INITCAP","INSTR","INSTRB","JSON_ARRAY","JSON_ARRAYAGG","JSON_OBJECT",
        "JSON_OBJECTAGG","JSON_QUERY","JSON_SERIALIZE","JSON_TABLE","JSON_VALUE","LAG","LAST_DAY","LAST_VALUE","LEAD","LEAST",
        "LENGTH","LENGTHB","LISTAGG","LN","LNNVL","LOCALTIMESTAMP","LOG","LOWER","LPAD","LTRIM","MAX","MEDIAN","MIN","MOD",
        "MONTHS_BETWEEN","NANVL","NEW_TIME","NEXT_DAY","NLSSORT","NLS_INITCAP","NLS_LOWER","NLS_UPPER","NTH_VALUE","NTILE",
        "NULLIF","NUMTODSINTERVAL","NUMTOYMINTERVAL","NVL","NVL2","ORA_HASH","PERCENT_RANK","PERCENTILE_CONT","PERCENTILE_DISC",
        "POWER","RANK","RATIO_TO_REPORT","RAWTOHEX","REGEXP_COUNT","REGEXP_INSTR","REGEXP_LIKE","REGEXP_REPLACE","REGEXP_SUBSTR",
        "REMAINDER","REPLACE","REVERSE","ROUND","ROW_NUMBER","RPAD","RTRIM","SESSIONTIMEZONE","SIGN","SIN","SINH","SOUNDEX",
        "SQRT","STANDARD_HASH","STDDEV","STDDEV_POP","STDDEV_SAMP","SUBSTR","SUBSTRB","SUM","SYS_CONNECT_BY_PATH","SYS_CONTEXT",
        "SYS_EXTRACT_UTC","SYS_GUID","SYSDATE","SYSTIMESTAMP","TAN","TANH","TO_BINARY_DOUBLE","TO_BINARY_FLOAT","TO_BLOB","TO_CHAR",
        "TO_CLOB","TO_DATE","TO_DSINTERVAL","TO_LOB","TO_MULTI_BYTE","TO_NCHAR","TO_NCLOB","TO_NUMBER","TO_SINGLE_BYTE",
        "TO_TIMESTAMP","TO_TIMESTAMP_TZ","TO_YMINTERVAL","TRANSLATE","TREAT","TRIM","TRUNC","TZ_OFFSET","UID","UNISTR","UPPER",
        "USERENV","VALIDATE_CONVERSION","VAR_POP","VAR_SAMP","VARIANCE","VSIZE","WIDTH_BUCKET","XMLAGG","XMLCAST","XMLELEMENT",
        "XMLFOREST","XMLQUERY","XMLSERIALIZE","XMLTABLE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>괄호 없이 쓰는 내장 함수(함수 대소문자).</summary>
    public static readonly FrozenSet<string> NoParenFunctions = new[]
    {
        "SYSDATE","SYSTIMESTAMP","CURRENT_DATE","CURRENT_TIMESTAMP","LOCALTIMESTAMP","DBTIMEZONE","SESSIONTIMEZONE","UID",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>렉서는 식별자로 주지만 키워드 대소문자를 쓰는 단어(점 앞뒤가 아닐 때만).</summary>
    public static readonly FrozenSet<string> ExtraKeywords = new[]
    {
        "ADD","AFTER","ALWAYS","AT","AUTHID","AUTONOMOUS_TRANSACTION","BEFORE","BINARY_DOUBLE","BINARY_FLOAT","BINARY_INTEGER",
        "BOTH","BYTE","CASCADE","CHECK","COLUMN","CONNECT","CONSTRAINT","CONSTRAINTS","CONSTRUCTOR","CUBE","CURRENT","CURRENT_USER",
        "CYCLE","DEFINER","DETERMINISTIC","DISABLE","EACH","ENABLE","ESCAPE","EXCEPTION_INIT","FALSE","FIRST","FLOAT",
        "FOLLOWING","FOREIGN","GENERATED","GLOBAL","IDENTITY","IGNORE","INCREMENT","INSTEAD","INTEGER","INTERSECT","ISOPEN",
        "KEEP","KEY","LAST","LEADING","LEVEL","LOCKED","LONG","MAXVALUE","MEMBER","MINVALUE","MODIFY",
        "NCHAR","NCLOB","NEXT","NOCACHE","NOCOPY","NOCYCLE","NOTFOUND","FOUND","NOWAIT","NULLS","NVARCHAR2","OBJECT","OFFSET",
        "ONLY","OVER","PARTITION","PIPELINED","PIVOT","PRECEDING","PRESERVE","PRIMARY","RAISE_APPLICATION_ERROR","RANGE","RAW",
        "REFERENCES","REFERENCING","RESULT_CACHE","ROLLUP","ROW","ROWCOUNT","ROWID","ROWNUM","ROWS","SELF","SEQUENCE",
        "SETS","SIBLINGS","SKIP","SQLCODE","SQLERRM","START","STATIC","TEMPORARY","TRAILING","TRUE","UNBOUNDED","UNDER",
        "UNIQUE","UNPIVOT","VARCHAR","WAIT","WITHIN","WORK","EXCEPT","FETCH","FORCE","NOFORCE","EDITIONABLE",
        "NONEDITIONABLE","MATERIALIZED","INSERTING","UPDATING","DELETING","LANGUAGE","EXTERNAL","OVERRIDING",
        "INSTANTIABLE","RETURN","EXPLAIN","PLAN","NATURAL","EXCLUSIVE","SHARE","MODE","CONTINUE","SQL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>키워드 뒤 괄호에 공백을 두지 않는 것(자료형·함수형 키워드).</summary>
    public static readonly FrozenSet<string> KeywordsTightParen = new[]
    {
        "CHAR","NUMBER","VARCHAR2","TIMESTAMP","INTERVAL","TABLE","REPLACE","VECTOR","VARRAY","JSON","DATE","LIMIT","ROWTYPE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>식별자이지만 뒤 괄호 앞에 공백을 두는 것.</summary>
    public static readonly FrozenSet<string> IdentifiersSpacedParen = new[]
    {
        "OVER","KEEP","PIVOT","UNPIVOT","KEY","CHECK","UNIQUE","OBJECT","WITHIN","PARTITION","INTERSECT","EXCEPT","ROLLUP","CUBE",
        "SETS","REFERENCES","RETURN","ONLY","ADD","MODIFY",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>이 단어 뒤 객체 이름 다음 괄호는 공백을 둔다(<c>INSERT INTO t (a)</c>).</summary>
    public static readonly FrozenSet<string> ObjectNameLeaders = new[]
    {
        "INTO","TABLE","VIEW","ON","REFERENCES","WITH",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 줄 처음에 오면 StatementSplitter 가 SQL*Plus 명령·REM 줄로 읽는 낱말. 원문에서 줄 가운데 있던 문장 첫 토큰이 이 낱말이면
    /// 줄을 바꾸지 않는다(바꾸면 토큰 열이 달라진다).
    /// </summary>
    public static readonly FrozenSet<string> SqlPlusLeads = new[]
    {
        "SET","DEFINE","DEF","UNDEFINE","UNDEF","PROMPT","PRO","SPOOL","SPO","WHENEVER","ACCEPT","ACC","VARIABLE","VAR","PRINT",
        "EXEC","EXECUTE","DESC","DESCRIBE","SHOW","COLUMN","COL","START","STA","CLEAR","CL","EXIT","QUIT","CONNECT","CONN",
        "DISCONNECT","HOST","TTITLE","BTITLE","STORE","PAUSE","TIMING","REPHEADER","REPFOOTER","BREAK","COMPUTE","SQLPLUS","REM","REMARK",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsSqlPlusLead(FormatItem t) => t.IsSym("@") || t.IsSym("/") || (t.IsWord && SqlPlusLeads.Contains(t.Word));

    public static string Apply(string s, LetterCase c) => c switch
    {
        LetterCase.Upper => s.ToUpperInvariant(),
        LetterCase.Lower => s.ToLowerInvariant(),
        _ => s,
    };
}
