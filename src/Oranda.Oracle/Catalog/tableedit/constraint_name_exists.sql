-- 테이블 편집 검사: 스키마에 그 이름의 제약이 있나(제약 이름은 스키마 안에서 하나). 바인드: owner, name.
-- id: tableedit.constraint_name_exists
-- min-version: 11
-- requires: dba_constraints, all_constraints
-- license: none
SELECT COUNT(*)
  FROM @PFX@_constraints c
 WHERE c.owner = :owner
   AND c.constraint_name = :name
