-- 제약(기본키·유일·외래키·검사). NOT NULL 검사 제약은 열 정보에 있으므로 뺀다. 바인드: owner, name
-- Oracle 과 달라진 점: search_condition_vc 열이 없어 search_condition 을 쓴다(결과 열 이름도 search_condition, Oracle 11g 변형과 같다).
-- Tibero 6 에서 search_condition 은 LIKE 로 비교할 수 있음을 확인했다. 읽는 쪽(BuildConstraints)도 NOT NULL 검사를 한 번 더 거른다.
-- id: meta.constraints
-- dbms: tibero
-- min-version: 6
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT c.constraint_name, c.constraint_type, c.status, c.search_condition, c.r_owner, rc.table_name AS r_table_name,
       cc.column_name, cc.position, c.r_constraint_name
  FROM @PFX@_constraints c
  LEFT JOIN @PFX@_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
  LEFT JOIN @PFX@_constraints rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.constraint_type IN ('P', 'U', 'R', 'C')
   AND NOT (c.constraint_type = 'C' AND c.search_condition LIKE '%IS NOT NULL')
 ORDER BY c.constraint_name, cc.position
