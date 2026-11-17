-- 열 형식 변환 검사(설계 18.9): 이 열이 든 PK·UK 를 참조하는 FK(열 바꿔치기 3단계 CASCADE CONSTRAINTS 가 함께 지워 버림 → ✖). 바인드: owner, name, column_name.
-- id: tableedit.referencing_fks
-- min-version: 11
-- requires: dba_constraints, all_constraints
-- license: none
SELECT DISTINCT c.owner, c.table_name, c.constraint_name
  FROM @PFX@_constraints c
  JOIN @PFX@_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name
  JOIN @PFX@_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name
 WHERE c.constraint_type = 'R'
   AND p.constraint_type IN ('P', 'U')
   AND p.owner = :owner
   AND p.table_name = :name
   AND pc.column_name = :column_name
 ORDER BY c.owner, c.table_name, c.constraint_name
