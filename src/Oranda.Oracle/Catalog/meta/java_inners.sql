-- Java 클래스의 내부 클래스(all_java_inners). 바인드: owner, name(긴 이름).
-- id: meta.java_inners
-- min-version: 11
-- requires: dba_java_inners, all_java_inners, jvm
-- license: none
SELECT n.inner_index, n.simple_name, n.full_name, n.accessibility, n.is_static
  FROM @PFX@_java_inners n
 WHERE n.owner = :owner
   AND n.name = :name
 ORDER BY n.inner_index
