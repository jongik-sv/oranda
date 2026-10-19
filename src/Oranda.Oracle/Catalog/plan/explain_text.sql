-- EXPLAIN PLAN 텍스트(DBMS_XPLAN.DISPLAY). 바인드: stmt_id, format
-- id: plan.explain_text
-- min-version: 19
-- requires: plan_table
-- license: none
SELECT plan_table_output
  FROM TABLE(DBMS_XPLAN.DISPLAY('PLAN_TABLE', :stmt_id, :format))
