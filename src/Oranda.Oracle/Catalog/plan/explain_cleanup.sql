-- EXPLAIN PLAN 이 남긴 행 지우기. 바인드: stmt_id
-- id: plan.explain_cleanup
-- min-version: 19
-- requires: plan_table
-- license: none
DELETE FROM plan_table WHERE statement_id = :stmt_id
