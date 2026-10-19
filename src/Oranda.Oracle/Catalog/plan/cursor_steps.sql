-- 공유 풀 커서의 실제 계획 단계(마지막 실행 통계 포함, 통계 수집이 꺼져 있으면 last_* 는 null).
-- 바인드: sql_id, inst_id(null=전체), child(null=전체). 자식이 여럿이면 호출 쪽이 첫 자식만 쓴다.
-- id: plan.cursor_steps
-- min-version: 19
-- requires: gv$sql_plan_statistics_all
-- license: none
-- rac: gv
SELECT p.inst_id, p.child_number, p.id, p.parent_id, p.depth, p.operation, p.options, p.object_owner, p.object_name,
       p.cost, p.cardinality, p.last_output_rows, p.last_starts, p.last_elapsed_time,
       p.access_predicates, p.filter_predicates, p.plan_hash_value
  FROM gv$sql_plan_statistics_all p
 WHERE p.sql_id = :sql_id
   AND (:inst_id IS NULL OR p.inst_id = :inst_id)
   AND (:child IS NULL OR p.child_number = :child)
 ORDER BY p.inst_id, p.child_number, p.id
