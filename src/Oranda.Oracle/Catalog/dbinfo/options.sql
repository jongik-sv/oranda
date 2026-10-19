-- 설치된 옵션(v$option).
-- id: dbinfo.options
-- min-version: 19
-- requires: v$option
-- license: none
SELECT o.parameter AS option_name, o.value
  FROM v$option o
 ORDER BY o.parameter
