-- 이 세션이 바로 앞에 실행한 문장의 계획 머리(DISPLAY_CURSOR 의 sql_id·child 를 비우면 직전 문장, 팩 불필요, TSK-06-04).
-- 머리 줄 「SQL_ID  xxxxxxxxxxxxx, child number n」 에서 커서를 읽는다(BASIC 형식은 이 줄을 빼므로 TYPICAL). 바인드 없음
-- id: plan.last_cursor_text
-- min-version: 11
-- requires: v$sql_plan, v$sql
-- license: none
SELECT plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(NULL, NULL, 'TYPICAL'))
