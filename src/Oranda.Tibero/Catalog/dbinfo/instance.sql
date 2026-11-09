-- 인스턴스 정보(한 행). 열 이름이 곧 항목 이름이다.
-- Oracle 11g 변형과 같은 열(version·edition, con_name 없음). Tibero 의 v$instance 에 없는 database_status·instance_role·logins·archiver 는 NULL.
-- edition 은 v$license.edition(예: standard), version 은 v$instance.version(주 버전 숫자). db_unique_name·service_name 은 SYS_CONTEXT 가 Tibero 에서 지원하지 않아(ODP-11006) NULL.
-- id: dbinfo.instance
-- dbms: tibero
-- min-version: 6
-- requires: v$instance, v$license
-- license: none
SELECT i.instance_number, i.instance_name, i.host_name, i.version,
       (SELECT MAX(l.edition) FROM v$license l) AS edition,
       i.status,
       CAST(NULL AS VARCHAR2(17)) AS database_status,
       CAST(NULL AS VARCHAR2(18)) AS instance_role,
       i.startup_time,
       CAST(NULL AS VARCHAR2(10)) AS logins,
       CAST(NULL AS VARCHAR2(7)) AS archiver,
       i.parallel,
       SYS_CONTEXT('USERENV', 'DB_NAME') AS db_name,
       CAST(NULL AS VARCHAR2(30)) AS db_unique_name,
       CAST(NULL AS VARCHAR2(64)) AS service_name,
       SYS_CONTEXT('USERENV', 'SESSION_USER') AS session_user
  FROM v$instance i
