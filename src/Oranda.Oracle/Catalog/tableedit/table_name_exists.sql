-- 테이블 편집 검사: 같은 이름 공간(테이블·뷰·시퀀스·시노님·PL/SQL·형식·머티리얼라이즈드 뷰)에 이름이 있나. 바인드: owner, name.
-- id: tableedit.table_name_exists
-- min-version: 11
-- requires: dba_objects, all_objects
-- license: none
SELECT COUNT(*)
  FROM @PFX@_objects o
 WHERE o.owner = :owner
   AND o.object_name = :name
   AND o.subobject_name IS NULL
   AND o.object_type IN ('TABLE', 'VIEW', 'SEQUENCE', 'SYNONYM', 'PROCEDURE', 'FUNCTION', 'PACKAGE', 'TYPE', 'MATERIALIZED VIEW', 'OPERATOR', 'CLUSTER')
