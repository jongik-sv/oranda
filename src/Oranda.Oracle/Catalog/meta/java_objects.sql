-- Java 저장 객체 목록(JAVA SOURCE·JAVA CLASS·JAVA RESOURCE). 한 소유자만 묻는다(SYS 쪽은 수만 개라 객체 목록 캐시에 넣지 않는다).
-- 바인드: owner(필수), types('|JAVA CLASS|' 형식, NULL = 셋 다), name_pattern(짧은 이름 LIKE), invalid_only(0/1), max_rows.
-- 긴 이름: max_rows 로 자른 뒤 짧은 이름이 '/' 로 시작하는(사전이 줄인) 줄만 푼다. @LONGNAME@ 은 호출 쪽이 DBMS_JAVA.LONGNAME(j.object_name) 또는 j.object_name 으로 바꾼다.
-- id: meta.java_objects
-- min-version: 11
-- requires: dba_objects, all_objects
-- license: none
SELECT j.owner, j.object_name, j.object_type, j.status,
       CASE WHEN j.object_name LIKE '/%' THEN @LONGNAME@ ELSE j.object_name END AS long_name
  FROM (SELECT o.owner, o.object_name, o.object_type, o.status
          FROM @PFX@_objects o
         WHERE o.owner = :owner
           AND o.object_type IN ('JAVA SOURCE', 'JAVA CLASS', 'JAVA RESOURCE')
           AND (:types IS NULL OR INSTR(:types, '|' || o.object_type || '|') > 0)
           AND (:name_pattern IS NULL OR o.object_name LIKE :name_pattern ESCAPE '\')
           AND (:invalid_only = 0 OR o.status <> 'VALID')
         ORDER BY o.object_type, o.object_name) j
 WHERE ROWNUM <= :max_rows
