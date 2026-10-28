-- Java 소스·클래스의 컴파일 오류(all_errors). 사전 이름이 줄었으면(긴 이름 128자 초과, '/' 로 시작) DBMS_JAVA.SHORTNAME 으로 찾는다.
-- Java 오류는 줄·열이 0 이고 javac 출력 한 줄이 한 행이다(「이름:줄: error: 메시지」, 소스 줄, ^, 설명…) — 호출 쪽이 묶는다. 바인드: owner, name, type
-- id: meta.java_errors
-- min-version: 11
-- requires: dba_errors, all_errors, dbms_java, jvm
-- license: none
SELECT e.line, e.position, e.text, e.attribute, e.message_number
  FROM @PFX@_errors e
 WHERE e.owner = :owner
   AND e.name IN (:name, DBMS_JAVA.SHORTNAME(:name))
   AND e.type = :type
 ORDER BY e.sequence
