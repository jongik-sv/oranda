-- 인스턴스 정보 대신 질의. v$instance 를 읽을 권한이 없는 계정용 — SYS_CONTEXT 만 써서 권한이 필요 없다.
-- 열 이름은 dbinfo.instance 와 맞춘다(그 질의에만 있는 열은 뺀다).
-- id: dbinfo.instance_basic
-- min-version: 19
-- requires: dual
-- license: none
SELECT SYS_CONTEXT('USERENV', 'INSTANCE') AS instance_number,
       SYS_CONTEXT('USERENV', 'INSTANCE_NAME') AS instance_name,
       SYS_CONTEXT('USERENV', 'SERVER_HOST') AS host_name,
       SYS_CONTEXT('USERENV', 'DB_NAME') AS db_name,
       SYS_CONTEXT('USERENV', 'DB_UNIQUE_NAME') AS db_unique_name,
       SYS_CONTEXT('USERENV', 'CON_NAME') AS con_name,
       SYS_CONTEXT('USERENV', 'SERVICE_NAME') AS service_name,
       SYS_CONTEXT('USERENV', 'SESSION_USER') AS session_user
  FROM dual
