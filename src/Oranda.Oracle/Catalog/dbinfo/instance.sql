-- 인스턴스 정보(한 행). 열 이름이 곧 항목 이름이다.
-- id: dbinfo.instance
-- min-version: 19
-- requires: v$instance
-- license: none
SELECT i.instance_number, i.instance_name, i.host_name, i.version_full, i.edition, i.status, i.database_status, i.instance_role,
       i.startup_time, i.logins, i.archiver, i.parallel,
       SYS_CONTEXT('USERENV', 'DB_NAME') AS db_name,
       SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME') AS db_unique_name,
       SYS_CONTEXT('USERENV', 'CON_NAME') AS con_name,
       SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS service_name,
       SYS_CONTEXT('USERENV', 'SESSION_USER') AS session_user
  FROM v$instance i
