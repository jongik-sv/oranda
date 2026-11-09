-- 공유 풀 커서의 실제 계획 단계(Tibero). V$SQL_PLAN_STATISTICS_ALL 이 없어 V$SQL_PLAN 과 V$SQL_PLAN_STATISTICS 를 SQL_ID·CHILD_NUMBER·ID 로 합친다.
-- 열 순서는 Oracle plan.cursor_steps 와 같다. OPTIONS 열이 없고 OPERATION 이 「index (full)」처럼 방식을 함께 담는다 → options = NULL.
-- 실행 통계(last_*)는 GATHER_SQL_PLAN_STAT=YES 일 때만 찬다(시험 서버 NO → 0). 바인드: sql_id, inst_id(null=전체), child(null=전체)
-- id: plan.cursor_steps
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_plan, v$sql_plan_statistics
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, p.child_number, p.id, p.parent_id, p.depth,
       p.operation, CAST(NULL AS VARCHAR2(30)) AS options, p.object_owner, p.object_name,
       p.cost, p.cardinality, st.last_output_rows, st.last_starts, st.last_elapsed_time,
       p.access_predicates, p.filter_predicates, p.plan_hash_value
  FROM v$sql_plan p
  LEFT JOIN v$sql_plan_statistics st
    ON st.sql_id = p.sql_id AND st.child_number = p.child_number AND st.id = p.id
 WHERE p.sql_id = :sql_id
   AND (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND (:child IS NULL OR p.child_number = :child)
 ORDER BY p.child_number, p.id
