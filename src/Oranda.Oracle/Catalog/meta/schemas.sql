-- 스키마(사용자) 이름 목록. @PFX@ 는 코드가 권한에 따라 dba / all 로 바꾼다.
-- id: meta.schemas
-- min-version: 11
-- requires: dba_users, all_users
-- license: none
SELECT u.username
  FROM @PFX@_users u
 ORDER BY u.username
