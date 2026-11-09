-- 문장 사본을 실행하지 않고 파싱만 하는 블록(하드 파스 → 10053 기록). 사본 글은 바인드 :oranda_sql 로 받는다.
-- id: app.trace_parse
-- min-version: 11
-- license: none
DECLARE
  c INTEGER := DBMS_SQL.OPEN_CURSOR;
BEGIN
  DBMS_SQL.PARSE(c, :oranda_sql, DBMS_SQL.NATIVE);
  DBMS_SQL.CLOSE_CURSOR(c);
EXCEPTION
  WHEN OTHERS THEN
    IF DBMS_SQL.IS_OPEN(c) THEN
      DBMS_SQL.CLOSE_CURSOR(c);
    END IF;
    RAISE;
END;
