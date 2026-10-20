-- 프로시저·함수 인자. 위치 0 의 이름 없는 행 = 함수 반환값. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- id: meta.arguments
-- min-version: 11
-- requires: dba_arguments, all_arguments
-- license: none
SELECT a.object_name AS procedure_name, a.overload, a.position, a.argument_name, a.in_out, a.data_type, a.defaulted,
       a.type_owner, a.type_name, a.type_subname
  FROM @PFX@_arguments a
 WHERE a.owner = :owner
   AND a.data_level = 0
   AND (a.package_name = :name OR (a.package_name IS NULL AND a.object_name = :name))
 ORDER BY a.object_name, a.overload, a.sequence
