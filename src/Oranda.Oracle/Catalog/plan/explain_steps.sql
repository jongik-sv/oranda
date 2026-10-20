-- EXPLAIN PLAN 결과 단계(PLAN_TABLE, 세션 전용 임시 테이블). 바인드: stmt_id
-- 열은 p.* 로 받아 이름으로 읽는다(OracleExecutionPlanService.ReadPlanTableRows). 사용자 스키마의 옛 PLAN_TABLE 에
-- plan_id·access_predicates 같은 열이 없어도 실패하지 않게 하기 위해서다(서버 버전 무관).
-- id: plan.explain_steps
-- min-version: 11
-- requires: plan_table
-- license: none
SELECT p.*
  FROM plan_table p
 WHERE p.statement_id = :stmt_id
 ORDER BY p.id
