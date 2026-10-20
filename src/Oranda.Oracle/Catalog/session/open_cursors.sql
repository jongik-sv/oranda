-- 세션이 연 커서(gv$open_cursor, 팩 불필요). 최근 실행한 것부터 200개까지. 바인드: inst_id, sid
-- id: session.open_cursors
-- min-version: 19
-- requires: gv$open_cursor
-- license: none
-- rac: gv
SELECT c.sql_id, c.cursor_type, c.last_sql_active_time, c.sql_text
  FROM gv$open_cursor c
 WHERE c.inst_id = :inst_id AND c.sid = :sid
 ORDER BY c.last_sql_active_time DESC NULLS LAST, c.sql_id
 FETCH FIRST 200 ROWS ONLY
