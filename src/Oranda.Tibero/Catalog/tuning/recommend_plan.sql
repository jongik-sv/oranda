-- 튜닝 추천: 한 SQL 계획(커서 하나)에서 눈여겨볼 줄만(Tibero) — 표 전체 스캔·인덱스 전체 스캔·카티션 조인.
-- Tibero V$SQL_PLAN 에 OPTIONS 열이 없고 OPERATION 이 「table access (full)」「index (full)」처럼 소문자 + 괄호 안 방식이다(실측).
-- 호출 쪽이 Oracle 모양으로 읽게 operation·options 로 나눠 대문자로 돌려준다: table access (full) → TABLE ACCESS / FULL,
-- index (full) → INDEX / FULL SCAN(Oracle 이름), 카티션(괄호 안 cartesian) → options CARTESIAN. 전체 스캔 표 크기는 @PFX@_TABLES 통계.
-- 바인드: inst_id, sql_id, plan_hash
-- id: tuning.recommend_plan
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_plan, dba_tables, all_tables
-- license: none
SELECT p.id,
       UPPER(CASE WHEN INSTR(p.operation, ' (') > 0 THEN SUBSTR(p.operation, 1, INSTR(p.operation, ' (') - 1) ELSE p.operation END) AS operation,
       CASE WHEN p.operation = 'index (full)' THEN 'FULL SCAN'
            WHEN LOWER(p.operation) LIKE '%(cartesian)%' THEN 'CARTESIAN'
            WHEN INSTR(p.operation, ' (') > 0
            THEN UPPER(SUBSTR(p.operation, INSTR(p.operation, ' (') + 2,
                              INSTR(p.operation, ')', INSTR(p.operation, ' (')) - INSTR(p.operation, ' (') - 2)) END AS options,
       p.object_owner, p.object_name, p.cardinality, t.num_rows, t.blocks
  FROM v$sql_plan p
  LEFT JOIN @PFX@_tables t ON p.operation = 'table access (full)' AND t.owner = p.object_owner AND t.table_name = p.object_name
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND p.sql_id = :sql_id
   AND p.plan_hash_value = :plan_hash
   AND p.child_number = (SELECT MIN(c.child_number) FROM v$sql_plan c
                          WHERE c.sql_id = :sql_id AND c.plan_hash_value = :plan_hash)
   AND (p.operation = 'table access (full)'
        OR p.operation = 'index (full)'
        OR LOWER(p.operation) LIKE '%(cartesian)%')
 ORDER BY p.id
