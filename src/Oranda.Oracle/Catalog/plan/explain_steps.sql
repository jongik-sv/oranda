-- EXPLAIN PLAN 결과 단계(PLAN_TABLE, 세션 전용 임시 테이블). 바인드: stmt_id
-- id: plan.explain_steps
-- min-version: 19
-- requires: plan_table
-- license: none
SELECT p.id, p.parent_id, p.depth, p.operation, p.options, p.object_owner, p.object_name,
       p.cost, p.cardinality, p.access_predicates, p.filter_predicates, p.plan_id
  FROM plan_table p
 WHERE p.statement_id = :stmt_id
 ORDER BY p.id
