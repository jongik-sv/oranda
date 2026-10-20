namespace Oranda.Language.Snippets;

/// <summary>코드 조각·옵티마이저 힌트·내장 함수·의사 컬럼 설명(EDIT-08, EDIT-10). 소유: TSK-04-07. 사용자 조각은 <see cref="UserSnippetFile"/>.</summary>
public static partial class SnippetLibrary
{
    /// <summary>삽입용 기본 조각.</summary>
    public static IReadOnlyList<Snippet> Snippets { get; } =
    [
        S("sel", "SELECT", "기본 조회", "SELECT ${1:*}\n  FROM ${2:table_name}\n WHERE ${3:1 = 1}$0"),
        S("selc", "SELECT COUNT", "건수 세기", "SELECT COUNT(*)\n  FROM ${1:table_name}\n WHERE ${2:1 = 1}$0"),
        S("selj", "SELECT JOIN", "두 테이블 조인", "SELECT ${3:a.*}\n  FROM ${1:table_a} a\n  JOIN ${2:table_b} b ON b.${4:id} = a.${5:id}\n WHERE ${6:1 = 1}$0"),
        S("selg", "SELECT GROUP BY", "그룹 집계", "SELECT ${1:col}, COUNT(*)\n  FROM ${2:table_name}\n GROUP BY ${1:col}\n ORDER BY COUNT(*) DESC$0"),
        S("selr", "SELECT 상위 N건", "FETCH FIRST 로 상위 N건", "SELECT ${1:*}\n  FROM ${2:table_name}\n ORDER BY ${3:1}\n FETCH FIRST ${4:10} ROWS ONLY$0"),
        S("ins", "INSERT", "한 건 입력", "INSERT INTO ${1:table_name} (${2:col1, col2})\nVALUES (${3:val1, val2})$0"),
        S("inss", "INSERT SELECT", "조회 결과 입력", "INSERT INTO ${1:table_name} (${2:col1, col2})\nSELECT ${3:col1, col2}\n  FROM ${4:source_table}\n WHERE ${5:1 = 1}$0"),
        S("upd", "UPDATE", "수정", "UPDATE ${1:table_name}\n   SET ${2:col} = ${3:value}\n WHERE ${4:id} = ${5:value}$0"),
        S("del", "DELETE", "삭제", "DELETE FROM ${1:table_name}\n WHERE ${2:id} = ${3:value}$0"),
        S("mrg", "MERGE", "있으면 수정, 없으면 입력", "MERGE INTO ${1:target} t\nUSING ${2:source} s\n   ON (t.${3:id} = s.${3:id})\n WHEN MATCHED THEN UPDATE SET t.${4:col} = s.${4:col}\n WHEN NOT MATCHED THEN INSERT (${3:id}, ${4:col}) VALUES (s.${3:id}, s.${4:col})$0"),
        S("with", "WITH", "공통 테이블 식", "WITH ${1:cte} AS (\n  SELECT ${2:*}\n    FROM ${3:table_name}\n)\nSELECT *\n  FROM ${1:cte}$0"),
        S("case", "CASE", "조건 분기", "CASE\n  WHEN ${1:cond} THEN ${2:result}\n  ELSE ${3:other}\nEND$0"),
        S("ct", "CREATE TABLE", "테이블 만들기", "CREATE TABLE ${1:table_name} (\n  ${2:id} NUMBER NOT NULL,\n  ${3:name} VARCHAR2(100),\n  CONSTRAINT pk_${1:table_name} PRIMARY KEY (${2:id})\n)$0"),
        S("cidx", "CREATE INDEX", "인덱스 만들기", "CREATE INDEX ${1:idx_name}\n    ON ${2:table_name} (${3:col})$0"),
        S("cv", "CREATE VIEW", "뷰 만들기", "CREATE OR REPLACE VIEW ${1:view_name} AS\nSELECT ${2:*}\n  FROM ${3:table_name}$0"),
        S("cseq", "CREATE SEQUENCE", "시퀀스 만들기", "CREATE SEQUENCE ${1:seq_name}\n  START WITH ${2:1}\n  INCREMENT BY ${3:1}\n  NOCACHE$0"),
        S("alt", "ALTER TABLE ADD", "컬럼 추가", "ALTER TABLE ${1:table_name}\n  ADD (${2:col} ${3:VARCHAR2(100)})$0"),
        S("expl", "EXPLAIN PLAN", "예상 실행 계획", "EXPLAIN PLAN FOR\n${1:SELECT 1 FROM dual};\nSELECT * FROM TABLE(DBMS_XPLAN.DISPLAY);$0"),
        S("anon", "익명 블록", "PL/SQL 익명 블록", "DECLARE\n  ${1:v_value} ${2:NUMBER};\nBEGIN\n  ${3:NULL;}\nEXCEPTION\n  WHEN OTHERS THEN\n    ${4:RAISE;}\nEND;\n/$0"),
        S("proc", "PROCEDURE", "프로시저", "CREATE OR REPLACE PROCEDURE ${1:proc_name} (${2:p_in IN NUMBER}) IS\nBEGIN\n  ${3:NULL;}\nEND ${1:proc_name};\n/$0"),
        S("func", "FUNCTION", "함수", "CREATE OR REPLACE FUNCTION ${1:func_name} (${2:p_in IN NUMBER})\n  RETURN ${3:NUMBER} IS\nBEGIN\n  RETURN ${4:NULL};\nEND ${1:func_name};\n/$0"),
        S("cur", "FOR 루프", "조회 결과 반복", "FOR ${1:r} IN (\n  SELECT ${2:*} FROM ${3:table_name}\n) LOOP\n  ${4:NULL;}\nEND LOOP;$0"),
        S("ifb", "IF", "IF 문", "IF ${1:cond} THEN\n  ${2:NULL;}\nELSE\n  ${3:NULL;}\nEND IF;$0"),
        S("exc", "EXCEPTION", "예외 처리", "EXCEPTION\n  WHEN NO_DATA_FOUND THEN\n    ${1:NULL;}\n  WHEN OTHERS THEN\n    ${2:RAISE;}$0"),
        S("trg", "TRIGGER", "트리거", "CREATE OR REPLACE TRIGGER ${1:trg_name}\n  BEFORE INSERT OR UPDATE ON ${2:table_name}\n  FOR EACH ROW\nBEGIN\n  ${3:NULL;}\nEND;\n/$0"),
    ];

    /// <summary>옵티마이저 힌트 설명.</summary>
    public static IReadOnlyList<Snippet> Hints { get; } =
    [
        H("FULL", "FULL(table)", "인덱스를 쓰지 않고 테이블 전체를 읽게 한다.", "SELECT /*+ FULL(e) */ * FROM emp e"),
        H("INDEX", "INDEX(table index)", "지정한 인덱스로 읽게 한다. 인덱스를 생략하면 옵티마이저가 고른다.", "SELECT /*+ INDEX(e emp_pk) */ * FROM emp e WHERE empno = 7369"),
        H("INDEX_DESC", "INDEX_DESC(table index)", "인덱스를 거꾸로(내림차순) 읽는다. 최근 건 조회에 쓴다.", "SELECT /*+ INDEX_DESC(e emp_pk) */ * FROM emp e WHERE ROWNUM = 1"),
        H("INDEX_FFS", "INDEX_FFS(table index)", "인덱스만 빠르게 전부 읽는다(테이블 접근 없음).", "SELECT /*+ INDEX_FFS(e emp_pk) */ COUNT(*) FROM emp e"),
        H("NO_INDEX", "NO_INDEX(table index)", "지정한 인덱스를 쓰지 않게 한다.", "SELECT /*+ NO_INDEX(e emp_pk) */ * FROM emp e"),
        H("LEADING", "LEADING(t1 t2 ...)", "조인 순서의 앞쪽 테이블을 정한다.", "SELECT /*+ LEADING(d e) USE_NL(e) */ * FROM dept d, emp e WHERE e.deptno = d.deptno"),
        H("ORDERED", "ORDERED", "FROM 절에 적은 순서대로 조인한다.", "SELECT /*+ ORDERED */ * FROM dept d, emp e WHERE e.deptno = d.deptno"),
        H("USE_NL", "USE_NL(table)", "그 테이블을 Nested Loops 조인으로 붙인다.", "SELECT /*+ USE_NL(e) */ * FROM dept d, emp e WHERE e.deptno = d.deptno"),
        H("USE_HASH", "USE_HASH(table)", "그 테이블을 해시 조인으로 붙인다.", "SELECT /*+ USE_HASH(e) */ * FROM dept d, emp e WHERE e.deptno = d.deptno"),
        H("USE_MERGE", "USE_MERGE(table)", "그 테이블을 정렬 병합 조인으로 붙인다.", "SELECT /*+ USE_MERGE(e) */ * FROM dept d, emp e WHERE e.deptno = d.deptno"),
        H("PARALLEL", "PARALLEL(table degree)", "병렬로 읽는다. 대용량 조회에 쓰며 서버 자원을 많이 쓴다.", "SELECT /*+ PARALLEL(e 4) */ COUNT(*) FROM emp e"),
        H("NO_PARALLEL", "NO_PARALLEL(table)", "병렬 실행을 막는다.", "SELECT /*+ NO_PARALLEL(e) */ COUNT(*) FROM emp e"),
        H("APPEND", "APPEND", "INSERT 를 직접 경로(direct-path)로 한다. 대량 입력에 빠르지만 커밋 전엔 같은 테이블을 읽지 못한다.", "INSERT /*+ APPEND */ INTO emp_hist SELECT * FROM emp"),
        H("FIRST_ROWS", "FIRST_ROWS(n)", "처음 n 건을 빨리 돌려주는 계획을 고른다.", "SELECT /*+ FIRST_ROWS(10) */ * FROM emp ORDER BY empno"),
        H("ALL_ROWS", "ALL_ROWS", "전체 처리량이 최선인 계획을 고른다.", "SELECT /*+ ALL_ROWS */ * FROM emp"),
        H("GATHER_PLAN_STATISTICS", "GATHER_PLAN_STATISTICS", "실행 중 실제 행 수를 모아 DBMS_XPLAN.DISPLAY_CURSOR 의 ALLSTATS 서식으로 볼 수 있게 한다.", "SELECT /*+ GATHER_PLAN_STATISTICS */ * FROM emp"),
        H("MATERIALIZE", "MATERIALIZE", "WITH 절 결과를 임시 테이블에 담아 재사용한다.", "WITH x AS (SELECT /*+ MATERIALIZE */ * FROM emp) SELECT * FROM x"),
        H("INLINE", "INLINE", "WITH 절을 임시 테이블 없이 쿼리에 풀어 넣는다.", "WITH x AS (SELECT /*+ INLINE */ * FROM emp) SELECT * FROM x"),
        H("RESULT_CACHE", "RESULT_CACHE", "결과를 서버 결과 캐시에 둔다.", "SELECT /*+ RESULT_CACHE */ deptno, COUNT(*) FROM emp GROUP BY deptno"),
        H("DRIVING_SITE", "DRIVING_SITE(table)", "DB 링크 조인을 그 테이블이 있는 쪽에서 한다.", "SELECT /*+ DRIVING_SITE(r) */ * FROM emp e, emp@remote r WHERE e.empno = r.empno"),
    ];

    /// <summary>내장 함수 설명(자주 쓰는 것).</summary>
    public static IReadOnlyList<Snippet> Functions { get; } =
    [
        F("NVL", "NVL(expr, 대체값)", "expr 이 NULL 이면 대체값을 돌려준다.", "SELECT NVL(comm, 0) FROM emp"),
        F("NVL2", "NVL2(expr, 값1, 값2)", "expr 이 NULL 이 아니면 값1, NULL 이면 값2.", "SELECT NVL2(comm, 'Y', 'N') FROM emp"),
        F("COALESCE", "COALESCE(expr1, expr2, ...)", "NULL 이 아닌 첫 값을 돌려준다.", "SELECT COALESCE(mobile, phone, 'none') FROM person"),
        F("NULLIF", "NULLIF(a, b)", "두 값이 같으면 NULL, 다르면 a.", "SELECT NULLIF(qty, 0) FROM item"),
        F("DECODE", "DECODE(expr, 검색1, 결과1, ..., 기본)", "값 비교 분기(Oracle 전용).", "SELECT DECODE(deptno, 10, 'A', 20, 'B', 'Z') FROM emp"),
        F("TO_CHAR", "TO_CHAR(값 [, 서식])", "숫자·날짜를 문자열로 바꾼다.", "SELECT TO_CHAR(SYSDATE, 'YYYY-MM-DD HH24:MI:SS') FROM dual"),
        F("TO_DATE", "TO_DATE(문자 [, 서식])", "문자열을 날짜로 바꾼다.", "SELECT TO_DATE('2026-01-31', 'YYYY-MM-DD') FROM dual"),
        F("TO_NUMBER", "TO_NUMBER(문자 [, 서식])", "문자열을 숫자로 바꾼다.", "SELECT TO_NUMBER('1,234.5', '9,999.9') FROM dual"),
        F("TO_TIMESTAMP", "TO_TIMESTAMP(문자 [, 서식])", "문자열을 TIMESTAMP 로 바꾼다.", "SELECT TO_TIMESTAMP('2026-01-31 10:00:00.123', 'YYYY-MM-DD HH24:MI:SS.FF3') FROM dual"),
        F("TRUNC", "TRUNC(값 [, 단위])", "날짜는 단위(DD, MM, YYYY 등)로, 숫자는 소수 자리로 자른다.", "SELECT TRUNC(SYSDATE, 'MM') FROM dual"),
        F("ROUND", "ROUND(값 [, 자리])", "반올림한다. 날짜도 된다.", "SELECT ROUND(123.456, 1) FROM dual"),
        F("ADD_MONTHS", "ADD_MONTHS(날짜, n)", "n 개월 뒤(앞) 날짜.", "SELECT ADD_MONTHS(SYSDATE, 1) FROM dual"),
        F("MONTHS_BETWEEN", "MONTHS_BETWEEN(날짜1, 날짜2)", "두 날짜 사이 개월 수.", "SELECT MONTHS_BETWEEN(SYSDATE, hiredate) FROM emp"),
        F("LAST_DAY", "LAST_DAY(날짜)", "그 달의 마지막 날.", "SELECT LAST_DAY(SYSDATE) FROM dual"),
        F("SUBSTR", "SUBSTR(문자, 시작 [, 길이])", "부분 문자열. 시작이 음수면 끝에서부터.", "SELECT SUBSTR('ABCDEF', 2, 3) FROM dual"),
        F("INSTR", "INSTR(문자, 찾는것 [, 시작 [, n번째]])", "찾는 문자열의 위치(없으면 0).", "SELECT INSTR('a,b,c', ',', 1, 2) FROM dual"),
        F("REPLACE", "REPLACE(문자, 찾는것 [, 바꿀것])", "문자열 바꾸기.", "SELECT REPLACE('a-b', '-', '_') FROM dual"),
        F("TRIM", "TRIM([LEADING|TRAILING|BOTH] [문자 FROM] 값)", "양쪽(또는 한쪽) 공백·지정 문자 제거.", "SELECT TRIM('  x  ') FROM dual"),
        F("LPAD", "LPAD(문자, 길이 [, 채울문자])", "왼쪽을 채워 길이를 맞춘다.", "SELECT LPAD(7, 3, '0') FROM dual"),
        F("RPAD", "RPAD(문자, 길이 [, 채울문자])", "오른쪽을 채워 길이를 맞춘다.", "SELECT RPAD('a', 3, '*') FROM dual"),
        F("LENGTH", "LENGTH(문자)", "글자 수.", "SELECT LENGTH('오라클') FROM dual"),
        F("UPPER", "UPPER(문자)", "대문자로.", "SELECT UPPER('abc') FROM dual"),
        F("LOWER", "LOWER(문자)", "소문자로.", "SELECT LOWER('ABC') FROM dual"),
        F("REGEXP_LIKE", "REGEXP_LIKE(문자, 패턴 [, 옵션])", "정규식과 맞으면 참(WHERE 에서).", "SELECT * FROM emp WHERE REGEXP_LIKE(ename, '^S.*T$')"),
        F("REGEXP_SUBSTR", "REGEXP_SUBSTR(문자, 패턴 [, 시작 [, n번째 [, 옵션 [, 그룹]]]])", "정규식과 맞는 부분 문자열.", "SELECT REGEXP_SUBSTR('a1b22', '[0-9]+', 1, 2) FROM dual"),
        F("REGEXP_REPLACE", "REGEXP_REPLACE(문자, 패턴 [, 바꿀것 ...])", "정규식으로 바꾸기.", "SELECT REGEXP_REPLACE('a  b', ' +', ' ') FROM dual"),
        F("LISTAGG", "LISTAGG(식 [, 구분자]) WITHIN GROUP (ORDER BY ...)", "그룹의 값을 구분자로 이어 붙인다.", "SELECT deptno, LISTAGG(ename, ',') WITHIN GROUP (ORDER BY ename) FROM emp GROUP BY deptno"),
        F("COUNT", "COUNT(* | [DISTINCT] 식)", "건수. COUNT(식) 은 NULL 을 세지 않는다.", "SELECT COUNT(DISTINCT deptno) FROM emp"),
        F("SUM", "SUM(식)", "합계.", "SELECT SUM(sal) FROM emp"),
        F("AVG", "AVG(식)", "평균(NULL 제외).", "SELECT AVG(sal) FROM emp"),
        F("MIN", "MIN(식)", "최솟값.", "SELECT MIN(sal) FROM emp"),
        F("MAX", "MAX(식)", "최댓값.", "SELECT MAX(sal) FROM emp"),
        F("ROW_NUMBER", "ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)", "그룹 안 순번(동점도 다른 번호).", "SELECT ename, ROW_NUMBER() OVER (PARTITION BY deptno ORDER BY sal DESC) rn FROM emp"),
        F("RANK", "RANK() OVER (ORDER BY ...)", "순위(동점은 같은 순위, 다음 번호를 건너뜀).", "SELECT ename, RANK() OVER (ORDER BY sal DESC) FROM emp"),
        F("DENSE_RANK", "DENSE_RANK() OVER (ORDER BY ...)", "순위(동점은 같은 순위, 번호를 건너뛰지 않음).", "SELECT ename, DENSE_RANK() OVER (ORDER BY sal DESC) FROM emp"),
        F("LAG", "LAG(식 [, 간격 [, 기본]]) OVER (...)", "앞 행의 값.", "SELECT sal, LAG(sal) OVER (ORDER BY hiredate) FROM emp"),
        F("LEAD", "LEAD(식 [, 간격 [, 기본]]) OVER (...)", "뒤 행의 값.", "SELECT sal, LEAD(sal) OVER (ORDER BY hiredate) FROM emp"),
        F("SYS_GUID", "SYS_GUID()", "고유 식별자(RAW 16).", "SELECT SYS_GUID() FROM dual"),
        F("CAST", "CAST(식 AS 형)", "자료형 바꾸기.", "SELECT CAST('12' AS NUMBER) FROM dual"),
        F("EXTRACT", "EXTRACT(YEAR|MONTH|DAY|HOUR FROM 날짜)", "날짜의 일부분을 숫자로.", "SELECT EXTRACT(YEAR FROM SYSDATE) FROM dual"),
        F("JSON_VALUE", "JSON_VALUE(문서, '$.경로')", "JSON 에서 값 하나를 꺼낸다.", "SELECT JSON_VALUE(doc, '$.name') FROM t"),
    ];

    /// <summary>의사 컬럼 설명.</summary>
    public static IReadOnlyList<Snippet> PseudoColumns { get; } =
    [
        P("ROWNUM", "ROWNUM", "조회된 순서대로 붙는 번호(1부터). ORDER BY 보다 먼저 매겨진다.", "SELECT * FROM (SELECT * FROM emp ORDER BY sal DESC) WHERE ROWNUM <= 5"),
        P("ROWID", "ROWID", "행의 물리 주소. 가장 빠른 행 접근 방법이지만 행이 옮겨지면 바뀔 수 있다.", "SELECT ROWID, empno FROM emp"),
        P("LEVEL", "LEVEL", "계층 질의(CONNECT BY)에서 깊이. 루트가 1.", "SELECT LEVEL, ename FROM emp START WITH mgr IS NULL CONNECT BY PRIOR empno = mgr"),
        P("SYSDATE", "SYSDATE", "DB 서버의 현재 날짜·시각(DATE).", "SELECT SYSDATE FROM dual"),
        P("SYSTIMESTAMP", "SYSTIMESTAMP", "DB 서버의 현재 시각(시간대 포함, 소수 초).", "SELECT SYSTIMESTAMP FROM dual"),
        P("CURRVAL", "시퀀스.CURRVAL", "시퀀스의 현재 값(같은 세션에서 NEXTVAL 을 먼저 불러야 한다).", "SELECT my_seq.CURRVAL FROM dual"),
        P("NEXTVAL", "시퀀스.NEXTVAL", "시퀀스의 다음 값.", "INSERT INTO t (id) VALUES (my_seq.NEXTVAL)"),
        P("ORA_ROWSCN", "ORA_ROWSCN", "행이 마지막으로 바뀐 때의 SCN(근사값).", "SELECT ORA_ROWSCN, empno FROM emp"),
        P("CONNECT_BY_ISLEAF", "CONNECT_BY_ISLEAF", "계층 질의에서 말단 행이면 1.", "SELECT ename, CONNECT_BY_ISLEAF FROM emp CONNECT BY PRIOR empno = mgr"),
        P("CONNECT_BY_ISCYCLE", "CONNECT_BY_ISCYCLE", "계층 질의에서 순환이 있으면 1(NOCYCLE 과 함께).", "SELECT ename, CONNECT_BY_ISCYCLE FROM emp CONNECT BY NOCYCLE PRIOR empno = mgr"),
        P("COLUMN_VALUE", "COLUMN_VALUE", "TABLE() 로 푼 컬렉션의 값 컬럼 이름.", "SELECT COLUMN_VALUE FROM TABLE(SYS.ODCINUMBERLIST(1, 2, 3))"),
        P("USER", "USER", "현재 접속한 사용자 이름.", "SELECT USER FROM dual"),
        P("UID", "UID", "현재 사용자의 번호.", "SELECT UID FROM dual"),
    ];

    /// <summary>전체(조각, 힌트, 함수, 의사 컬럼 순).</summary>
    public static IReadOnlyList<Snippet> All { get; } = [.. Snippets, .. Hints, .. Functions, .. PseudoColumns];

    /// <summary>
    /// 이름·줄임말·설명(영어 글 포함)에서 찾는다(대소문자 무시, 공백으로 나눈 낱말이 모두 들어 있어야 함). 줄임말이 같거나 앞이 같은 것이 먼저.
    /// </summary>
    public static IReadOnlyList<Snippet> Search(string? query, SnippetKind? kind = null, IEnumerable<Snippet>? user = null)
    {
        IEnumerable<Snippet> pool = user is null ? All : [.. All, .. user];
        var q = kind is null ? pool : pool.Where(s => s.Kind == kind);
        var words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 0)
        {
            // 화면 언어와 상관없이 한·영 이름과 설명을 모두 본다(영어 화면에서 한국어 낱말로도 찾게).
            q = q.Where(s => words.All(w => Has(s.Prefix, w) || Has(s.Name, w) || Has(s.Description, w)
                || Has(DisplayName(s, "en"), w) || Has(DisplayDescription(s, "en"), w)));
        }

        var first = words.Length > 0 ? words[0] : string.Empty;
        return q.OrderBy(s => first.Length == 0 ? 0 : string.Equals(s.Prefix, first, StringComparison.OrdinalIgnoreCase) ? 0 : s.Prefix.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ToList();
    }

    /// <summary>줄임말이 정확히 같은 조각(<see cref="SnippetKind.Snippet"/> 만). 사용자 조각이 기본을 덮는다.</summary>
    public static Snippet? FindByPrefix(string prefix, IEnumerable<Snippet>? user = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var u = user?.FirstOrDefault(s => s.Kind == SnippetKind.Snippet && string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
        return u ?? Snippets.FirstOrDefault(s => string.Equals(s.Prefix, prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Has(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static Snippet S(string prefix, string name, string description, string body) => new(prefix, name, description, body);

    private static Snippet H(string prefix, string name, string description, string example) => new(prefix, name, description, Esc(example), SnippetKind.Hint);

    private static Snippet F(string prefix, string name, string description, string example) => new(prefix, name, description, Esc(example), SnippetKind.Function);

    private static Snippet P(string prefix, string name, string description, string example) => new(prefix, name, description, Esc(example), SnippetKind.PseudoColumn);

    // 예제는 자리 표시자가 아니라 그대로 보여 줄 글자라 $ 를 막는다.
    private static string Esc(string example) => example.Replace("$", "\\$", StringComparison.Ordinal);
}
