-- 세션이 연 커서(11g 변형: FETCH FIRST 대신 ROWNUM 감싸기). 바인드: inst_id, sid
-- id: session.open_cursors
-- min-version: 11
-- requires: gv$open_cursor
-- license: none
-- rac: gv
SELECT sql_id, cursor_type, last_sql_active_time, sql_text
  FROM (SELECT c.sql_id, c.cursor_type, c.last_sql_active_time, c.sql_text
          FROM gv$open_cursor c
         WHERE c.inst_id = :inst_id AND c.sid = :sid
         ORDER BY c.last_sql_active_time DESC NULLS LAST, c.sql_id)
 WHERE ROWNUM <= 200
