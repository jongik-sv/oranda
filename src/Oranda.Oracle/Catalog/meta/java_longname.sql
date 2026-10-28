-- Java 객체의 짧은 이름(object_name) → 긴 이름(예 com/example/Util). 바인드: name. 권한·JVM 이 없으면 실패하고 호출 쪽이 짧은 이름을 쓴다.
-- id: meta.java_longname
-- min-version: 11
-- requires: dbms_java, jvm
-- license: none
SELECT DBMS_JAVA.LONGNAME(:name) FROM dual
