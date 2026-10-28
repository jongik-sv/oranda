-- Java 클래스 한 개(all_java_classes). 이름은 긴 이름. 바인드: owner, name(긴 이름).
-- id: meta.java_class
-- min-version: 11
-- requires: dba_java_classes, all_java_classes, jvm
-- license: none
SELECT c.kind, c.accessibility, c.is_abstract, c.is_final, c.is_static, c.is_inner, c.super, c.outer, c.source
  FROM @PFX@_java_classes c
 WHERE c.owner = :owner
   AND c.name = :name
