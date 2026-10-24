-- AWR 에 남은 한 plan_hash 의 계획 단계(TN-13, 두 plan_hash 비교용 — 화면 TSK-06-06). Diagnostics Pack 필요. 바인드: sql_id, plan_hash_value
-- id: tuning.awr_plan_steps
-- min-version: 11
-- requires: dba_hist_sql_plan
-- license: diagnostics
SELECT p.id, p.parent_id, p.depth, p.operation, p.options, p.object_owner, p.object_name, p.cost, p.cardinality,
       p.access_predicates, p.filter_predicates
  FROM dba_hist_sql_plan p
 WHERE p.sql_id = :sql_id
   AND p.plan_hash_value = :plan_hash_value
   AND p.dbid = (SELECT d.dbid FROM v$database d)
 ORDER BY p.id
