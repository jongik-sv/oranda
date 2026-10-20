-- 제약(11g 변형). 바인드: owner, name
-- 11g 에는 search_condition_vc 가 없고 search_condition 은 LONG 이다. LONG 은 WHERE 에 쓸 수 없으므로
-- NOT NULL 검사 제약은 여기서 거르지 못하고 읽는 쪽(OracleMetadataProvider.BuildConstraints)이 뺀다.
-- LONG 은 InitialLONGFetchSize = -1 로 읽는다.
-- id: meta.constraints
-- min-version: 11
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
 ORDER BY c.constraint_name, cc.position
