-- 튜닝 추천: 한 SQL 계획(커서 하나)에서 눈여겨볼 줄만(TSK-14-17, D-051) — 전체 스캔·INDEX FULL SCAN·카티션 조인(options = CARTESIAN).
-- 고른 SQL 과 점수 상위 몇 개에만 보낸다(sql_id·plan_hash 로 좁힘). 전체 스캔 표의 크기는 딕셔너리 통계(num_rows·blocks)로 본다.
-- 바인드: inst_id, sql_id, plan_hash
-- id: tuning.recommend_plan
-- min-version: 11
-- requires: gv$sql_plan
-- license: none
-- rac: gv
SELECT p.id, p.operation, p.options, p.object_owner, p.object_name, p.cardinality, t.num_rows, t.blocks
  FROM gv$sql_plan p
  LEFT JOIN @PFX@_tables t ON p.operation = 'TABLE ACCESS' AND t.owner = p.object_owner AND t.table_name = p.object_name
 WHERE p.inst_id = :inst_id
   AND p.sql_id = :sql_id
   AND p.plan_hash_value = :plan_hash
   AND p.child_number = (SELECT MIN(c.child_number) FROM gv$sql_plan c
                          WHERE c.inst_id = :inst_id AND c.sql_id = :sql_id AND c.plan_hash_value = :plan_hash)
   AND ((p.operation = 'TABLE ACCESS' AND p.options LIKE '%FULL%')
        OR (p.operation = 'INDEX' AND p.options = 'FULL SCAN')
        OR p.options = 'CARTESIAN')
 ORDER BY p.id
