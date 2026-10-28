-- Java 클래스가 구현하는 인터페이스(all_java_implements). 바인드: owner, name(긴 이름).
-- id: meta.java_implements
-- min-version: 11
-- requires: dba_java_implements, all_java_implements
-- license: none
SELECT i.interface_index, i.interface_name
  FROM @PFX@_java_implements i
 WHERE i.owner = :owner
   AND i.name = :name
 ORDER BY i.interface_index
