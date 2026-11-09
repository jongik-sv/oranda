-- 데이터베이스 정보(한 행). 열 이름이 곧 항목 이름이다.
-- Oracle 11g 변형과 같은 열(cdb 없음). Tibero 의 v$database 에 없는 database_role·force_logging·flashback_on 은 NULL, created 는 create_date.
-- id: dbinfo.database
-- dbms: tibero
-- min-version: 6
-- requires: v$database
-- license: none
SELECT d.name, d.dbid, d.create_date AS created, d.log_mode, d.open_mode,
       CAST(NULL AS VARCHAR2(16)) AS database_role,
       CAST(NULL AS VARCHAR2(39)) AS force_logging,
       CAST(NULL AS VARCHAR2(18)) AS flashback_on,
       d.platform_name
  FROM v$database d
