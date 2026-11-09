-- 이 세션이 바로 앞에 실행한 문장의 계획(Tibero DISPLAY_CURSOR(NULL, NULL, 'TYPICAL')). 반환 열 COLUMN_VALUE 를 Oracle 과 같은 이름으로 바꾼다.
-- 주의: 머리 줄 모양이 Oracle(「SQL_ID  xxx, child number n」)과 다르다 — Tibero 는 「SQL ID         : xxx」 줄이고 자식 번호 줄이 없다(실측).
-- 머리에서 커서를 읽는 쪽은 Tibero 모양을 따로 읽어야 한다. 바인드 없음
-- id: plan.last_cursor_text
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_plan, v$sql
-- license: none
SELECT column_value AS plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(NULL, NULL, 'TYPICAL'))
