-- Java 메서드 인자(all_java_arguments). 메서드 서명을 만들 때 쓴다. 바인드: owner, name(긴 이름).
-- id: meta.java_arguments
-- min-version: 11
-- requires: dba_java_arguments, all_java_arguments, jvm
-- license: none
SELECT a.method_index, a.argument_position, a.array_depth, a.base_type, a.argument_class
  FROM @PFX@_java_arguments a
 WHERE a.owner = :owner
   AND a.name = :name
 ORDER BY a.method_index, a.argument_position
