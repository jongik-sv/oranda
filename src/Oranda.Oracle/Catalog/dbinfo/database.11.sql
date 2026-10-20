-- 데이터베이스 정보(11g 변형). 11g 의 v$database 에는 cdb 열이 없어(다중 테넌트 없음) 뺀다. 열 이름이 곧 항목 이름이다.
-- id: dbinfo.database
-- min-version: 11
-- requires: v$database
-- license: none
SELECT d.name, d.dbid, d.created, d.log_mode, d.open_mode, d.database_role, d.force_logging, d.flashback_on, d.platform_name
  FROM v$database d
