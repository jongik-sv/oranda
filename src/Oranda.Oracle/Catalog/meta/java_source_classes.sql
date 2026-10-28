-- JAVA SOURCE 를 컴파일해 나온 클래스들(all_java_derivations 로 소스 긴 이름 → 클래스, 정보는 all_java_classes). 바인드: owner, name(소스 긴 이름).
-- id: meta.java_source_classes
-- min-version: 11
-- requires: dba_java_classes, all_java_classes, dba_java_derivations, all_java_derivations, jvm
-- license: none
SELECT c.name, c.kind, c.accessibility, c.is_abstract, c.is_final, c.is_inner
  FROM @PFX@_java_derivations d, @PFX@_java_classes c
 WHERE d.owner = :owner
   AND d.source_name = :name
   AND c.owner = d.owner
   AND c.name = d.class_name
 ORDER BY c.name
