-- 서버 버전 대신 질의. v$version 을 읽을 권한이 없는 계정용 — PRODUCT_COMPONENT_VERSION 은 PUBLIC 이다. 결과 열은 banner 하나(dbinfo.version 과 같음).
-- id: dbinfo.version_basic
-- min-version: 11
-- requires: product_component_version
-- license: none
SELECT p.product || ' ' || p.version || ' - ' || p.status AS banner
  FROM product_component_version p
