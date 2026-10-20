-- 이 테이블의 기본키·유일 키를 참조하는 외래키(다른 테이블 또는 자기 자신). 바인드: owner, name
-- id: meta.detail_referenced_by
-- min-version: 19
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT c.owner, c.table_name, c.constraint_name,
       (SELECT LISTAGG(cc.column_name, ', ') WITHIN GROUP (ORDER BY cc.position)
          FROM @PFX@_cons_columns cc
         WHERE cc.owner = c.owner
           AND cc.constraint_name = c.constraint_name) AS column_names,
       r.constraint_name AS r_constraint_name, c.delete_rule, c.status
  FROM @PFX@_constraints c
  JOIN @PFX@_constraints r ON r.owner = c.r_owner AND r.constraint_name = c.r_constraint_name
 WHERE r.owner = :owner
   AND r.table_name = :name
   AND r.constraint_type IN ('P', 'U')
   AND c.constraint_type = 'R'
 ORDER BY c.owner, c.table_name, c.constraint_name
