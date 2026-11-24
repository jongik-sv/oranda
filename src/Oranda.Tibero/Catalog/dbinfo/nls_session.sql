-- NLS 설정 대신 질의(세션만). nls_database_parameters 를 읽을 권한이 없는 계정용 — 실측(2026-11-10) 그런 계정도 nls_session_parameters 는 읽힌다.
-- 열은 dbinfo.nls 와 같다(scope, parameter, value).
-- id: dbinfo.nls_session
-- dbms: tibero
-- min-version: 6
-- requires: nls_session_parameters
-- license: none
SELECT 'SESSION' AS scope, s.parameter, s.value
  FROM nls_session_parameters s
 ORDER BY s.parameter
