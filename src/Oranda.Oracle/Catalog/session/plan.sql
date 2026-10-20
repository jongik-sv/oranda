-- 현재 커서의 실제 계획(DBMS_XPLAN.DISPLAY_CURSOR, 팩 불필요 형식 TYPICAL). 접속한 인스턴스의 커서만 보인다. 바인드: sql_id, child
-- id: session.plan
-- min-version: 11
-- requires: v$sql_plan, v$sql
-- license: none
SELECT plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(:sql_id, :child, 'TYPICAL'))
