-- NLS 설정(데이터베이스·세션).
-- id: dbinfo.nls
-- min-version: 19
-- requires: nls_database_parameters, nls_session_parameters
-- license: none
SELECT scope, parameter, value
  FROM (SELECT 'DATABASE' AS scope, d.parameter, d.value FROM nls_database_parameters d
        UNION ALL
        SELECT 'SESSION', s.parameter, s.value FROM nls_session_parameters s)
 ORDER BY parameter, scope
