-- 현재 커서의 실제 계획(Tibero DBMS_XPLAN.DISPLAY_CURSOR, 형식 TYPICAL). Tibero 의 반환 열은 COLUMN_VALUE 라 Oracle 과 같은 이름으로 바꾼다.
-- 줄 모양(머리 「SQL ID : …」, 표 열)은 Oracle 과 다르다. 바인드: sql_id, child
-- id: session.plan
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_plan, v$sql
-- license: none
SELECT column_value AS plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(:sql_id, :child, 'TYPICAL'))
