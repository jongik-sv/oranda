-- Java 클래스를 만든 JAVA SOURCE(all_java_derivations: 소스 긴 이름 → 클래스 긴 이름)의 짧은 이름. 같은 스키마만 본다. 바인드: owner, name(클래스 긴 이름).
-- all_java_classes.source 는 소스 객체 이름이 아니라 소스 파일 표시(긴 이름의 마지막 조각, 예 com/example/Hello → Hello)라 쓰지 않는다(JVM 서버 확인 2026-10-28).
-- id: meta.java_source_object
-- min-version: 11
-- requires: dba_objects, all_objects, dba_java_derivations, all_java_derivations, dbms_java, jvm
-- license: none
SELECT o.object_name
  FROM @PFX@_java_derivations d, @PFX@_objects o
 WHERE d.owner = :owner
   AND d.class_name = :name
   AND o.owner = d.owner
   AND o.object_type = 'JAVA SOURCE'
   AND o.object_name IN (d.source_name, DBMS_JAVA.SHORTNAME(d.source_name))
   AND ROWNUM = 1
