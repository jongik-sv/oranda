-- 인스턴스 정보 대신 질의. v$instance 를 읽을 권한이 없는 계정용(Tibero 는 「객체 없음」 ODP-8033 으로 답한다) — SYS_CONTEXT 만 써서 권한이 필요 없다.
-- 열 이름은 dbinfo.instance 와 맞춘다. 쓰는 키는 Tibero 에서 되는 것만(docs/design/tibero-driver-session.md 3장; DB_UNIQUE_NAME·SERVICE_NAME·DB_DOMAIN 은 ODP-11006).
-- 실측(2026-11-10, CONNECT·RESOURCE 만 있는 계정): v$instance·v$version·product_component_version 은 ODP-8033, 이 질의는 읽힘.
-- id: dbinfo.instance_basic
-- dbms: tibero
-- min-version: 6
-- requires: dual
-- license: none
SELECT SYS_CONTEXT('USERENV', 'INSTANCE') AS instance_number,
       SYS_CONTEXT('USERENV', 'INSTANCE_NAME') AS instance_name,
       SYS_CONTEXT('USERENV', 'SERVER_HOST') AS host_name,
       SYS_CONTEXT('USERENV', 'DB_NAME') AS db_name,
       SYS_CONTEXT('USERENV', 'SESSION_USER') AS session_user
  FROM dual
