-- Java 클래스의 소스(all_java_classes.source)가 같은 스키마의 JAVA SOURCE 객체면 그 짧은 이름. 바인드: owner, source(긴 이름).
-- id: meta.java_source_object
-- min-version: 11
-- requires: dba_objects, all_objects, dbms_java, jvm
-- license: none
SELECT o.object_name
  FROM @PFX@_objects o
 WHERE o.owner = :owner
   AND o.object_type = 'JAVA SOURCE'
   AND o.object_name IN (:source, DBMS_JAVA.SHORTNAME(:source))
   AND ROWNUM = 1
