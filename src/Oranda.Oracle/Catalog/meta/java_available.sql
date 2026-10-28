-- 서버에 Java(JVM)가 있는지: SYS.DBMS_JAVA 패키지가 보이면 있다(없으면 Java 객체도 만들 수 없다, ORA-29538). 스키마 브라우저가 접속마다 한 번 묻는다. 바인드 없음.
-- id: meta.java_available
-- min-version: 11
-- requires: dba_objects, all_objects
-- license: none
SELECT COUNT(*)
  FROM @PFX@_objects o
 WHERE o.owner = 'SYS'
   AND o.object_name = 'DBMS_JAVA'
   AND o.object_type = 'PACKAGE'
