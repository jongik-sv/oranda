-- 테이블 편집 검사: 이 테이블을 쓰는 객체(뷰·트리거·PL/SQL·시노님, DROP·이름 바꾸기 ⚠). 바인드: owner, name.
-- id: tableedit.dependents
-- min-version: 11
-- requires: dba_dependencies, all_dependencies
-- license: none
SELECT DISTINCT d.owner, d.name, d.type
  FROM @PFX@_dependencies d
 WHERE d.referenced_owner = :owner
   AND d.referenced_name = :name
   AND d.referenced_type = 'TABLE'
 ORDER BY d.owner, d.name, d.type
