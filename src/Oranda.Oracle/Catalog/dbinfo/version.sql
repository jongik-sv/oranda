-- 서버 버전 배너.
-- id: dbinfo.version
-- min-version: 19
-- requires: v$version
-- license: none
SELECT v.banner_full AS banner
  FROM v$version v
