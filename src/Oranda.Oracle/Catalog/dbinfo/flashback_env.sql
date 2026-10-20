-- 플래시백 조회(AS OF·VERSIONS)가 거슬러 갈 수 있는 시간: undo_retention(초).
-- id: dbinfo.flashback_env
-- min-version: 19
-- requires: v$parameter
-- license: none
SELECT p.value AS undo_retention
  FROM v$parameter p
 WHERE p.name = 'undo_retention'
