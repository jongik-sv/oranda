-- NLS 설정 대신 질의(세션만). nls_database_parameters 를 읽지 못하는 계정용 — nls_session_parameters 는 PUBLIC 이다.
-- 열은 dbinfo.nls 와 같다(scope, parameter, value).
-- id: dbinfo.nls_session
-- min-version: 11
-- requires: nls_session_parameters
-- license: none
SELECT 'SESSION' AS scope, s.parameter, s.value
  FROM nls_session_parameters s
 ORDER BY s.parameter
