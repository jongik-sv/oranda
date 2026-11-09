-- 실제 계획 텍스트(Tibero DBMS_XPLAN.DISPLAY_CURSOR(IN_SQL_ID, IN_CHILD_NO, FORMAT)). 반환 열 COLUMN_VALUE 를 Oracle 과 같은 이름으로 바꾼다.
-- 줄 모양은 Oracle 과 다르다(머리 「SQL ID : …」, 표에 Rows·Elaps. Time·Starts 열). 바인드: sql_id, child, format
-- id: plan.cursor_text
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_plan, v$sql
-- license: none
SELECT column_value AS plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(:sql_id, :child, :format))
