-- JAVA SOURCE 를 컴파일해 나온 클래스들(all_java_classes.source = 소스 긴 이름). 바인드: owner, name(소스 긴 이름).
-- id: meta.java_source_classes
-- min-version: 11
-- requires: dba_java_classes, all_java_classes, jvm
-- license: none
SELECT c.name, c.kind, c.accessibility, c.is_abstract, c.is_final, c.is_inner
  FROM @PFX@_java_classes c
 WHERE c.owner = :owner
   AND c.source = :name
 ORDER BY c.name
