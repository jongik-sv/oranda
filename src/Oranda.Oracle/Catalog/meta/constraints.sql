-- 제약(기본키·유일·외래키·검사). NOT NULL 검사 제약은 열 정보에 있으므로 뺀다. 바인드: owner, name
-- 그 뒤 열(테이블 편집기 TE-29): delete_rule(FK), r_column_name(FK 참조 열, 같은 위치), index_name(PK·UK 가 쓰는 인덱스).
-- id: meta.constraints
-- min-version: 19
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT c.constraint_name, c.constraint_type, c.status, c.search_condition_vc, c.r_owner, rc.table_name AS r_table_name,
       cc.column_name, cc.position, c.r_constraint_name,
       c.delete_rule, rcc.column_name AS r_column_name, c.index_name
  FROM @PFX@_constraints c
  LEFT JOIN @PFX@_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
  LEFT JOIN @PFX@_constraints rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
  LEFT JOIN @PFX@_cons_columns rcc
    ON rcc.owner = c.r_owner AND rcc.constraint_name = c.r_constraint_name AND rcc.position = cc.position
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.constraint_type IN ('P', 'U', 'R', 'C')
   AND NOT (c.constraint_type = 'C' AND c.search_condition_vc LIKE '%IS NOT NULL')
 ORDER BY c.constraint_name, cc.position
