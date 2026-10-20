-- 서버 버전 배너(11g 변형: banner_full 이 없어 banner 를 쓴다).
-- id: dbinfo.version
-- min-version: 11
-- requires: v$version
-- license: none
SELECT v.banner
  FROM v$version v
