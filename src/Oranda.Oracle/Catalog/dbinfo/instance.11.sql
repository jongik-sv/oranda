-- 인스턴스 정보(11g 변형). 11g 의 v$instance 에는 version_full·edition 이 없어 version 과 v$version 배너로 대신한다.
-- 다중 테넌트(CON_NAME)도 없어 뺀다. 열 이름이 곧 항목 이름이다.
-- id: dbinfo.instance
-- min-version: 11
-- requires: v$instance, v$version
-- license: none
SELECT i.instance_number, i.instance_name, i.host_name, i.version,
       (SELECT CASE WHEN MAX(CASE WHEN v.banner LIKE '%Enterprise Edition%' THEN 1 END) = 1 THEN 'EE'
                    WHEN MAX(CASE WHEN v.banner LIKE '%Express Edition%' THEN 1 END) = 1 THEN 'XE'
                    ELSE 'SE' END
          FROM v$version v) AS edition,
       i.status, i.database_status, i.instance_role,
       i.startup_time, i.logins, i.archiver, i.parallel,
       SYS_CONTEXT('USERENV', 'DB_NAME') AS db_name,
       SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME') AS db_unique_name,
       SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS service_name,
       SYS_CONTEXT('USERENV', 'SESSION_USER') AS session_user
  FROM v$instance i
