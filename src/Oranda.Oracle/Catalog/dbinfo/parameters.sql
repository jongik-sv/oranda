-- 초기화 파라미터(v$parameter).
-- id: dbinfo.parameters
-- min-version: 19
-- requires: v$parameter
-- license: none
SELECT p.name, p.display_value AS value, p.isdefault, p.ismodified, p.isses_modifiable, p.issys_modifiable, p.description
  FROM v$parameter p
 ORDER BY p.name
