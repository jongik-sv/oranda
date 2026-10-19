-- 데이터베이스 정보(한 행). 열 이름이 곧 항목 이름이다.
-- id: dbinfo.database
-- min-version: 19
-- requires: v$database
-- license: none
SELECT d.name, d.dbid, d.created, d.log_mode, d.open_mode, d.database_role, d.cdb, d.force_logging, d.flashback_on, d.platform_name
  FROM v$database d
