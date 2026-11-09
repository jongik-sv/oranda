-- 이력에 남은 한 plan_hash 의 계획 단계(Tibero DBA_HIST_SQL_PLAN). 열은 Oracle tuning.awr_plan_steps 와 같다.
-- SQL_ID 대신 SQL_HASH_VALUE(sql_id 아래 32비트를 SQL 로 계산, awr_plan_history 와 같은 식), DBID 없음.
-- OPTIONS 열이 없고 OPERATION 이 「table access (full)」처럼 방식을 함께 담는다 → options = NULL(plan.cursor_steps 와 같은 규칙).
-- Tibero 는 관리 팩이 없어 license: none. 바인드: sql_id, plan_hash_value
-- id: tuning.awr_plan_steps
-- dbms: tibero
-- min-version: 6
-- requires: dba_hist_sql_plan
-- license: none
SELECT p.id, p.parent_id, p.depth, p.operation, CAST(NULL AS VARCHAR2(30)) AS options, p.object_owner, p.object_name,
       p.cost, p.cardinality, p.access_predicates, p.filter_predicates
  FROM dba_hist_sql_plan p
 WHERE p.sql_hash_value = (SELECT MOD(SUM((INSTR('0123456789abcdfghjkmnpqrstuvwxyz', SUBSTR(:sql_id, LEVEL, 1)) - 1)
                                          * POWER(32, LENGTH(:sql_id) - LEVEL)), 4294967296)
                             FROM dual
                           CONNECT BY LEVEL <= LENGTH(:sql_id))
   AND p.plan_hash_value = :plan_hash_value
 ORDER BY p.id
