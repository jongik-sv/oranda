-- 표 다시 만들기: 트리거 한 개의 DDL. 열 = DDL 글. 바인드: NAME(트리거 이름), OWN(트리거 소유자).
-- DBMS_METADATA.GET_DDL 을 SELECT 로 부른다(PL/SQL 블록이 아니다).
-- id: copy.recreate_trigger_ddl
-- min-version: 11
-- requires: dbms_metadata
-- license: none
SELECT DBMS_METADATA.GET_DDL('TRIGGER', :NAME, :OWN) FROM DUAL
