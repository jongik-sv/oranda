-- 세션이 연 커서(Tibero V$OPEN_CURSOR). 열 순서는 Oracle session.open_cursors 와 같다.
-- Tibero 에 없는 열: CURSOR_TYPE·LAST_SQL_ACTIVE_TIME → NULL. 최근 연 것(CURSOR_ID 큰 것)부터 200개까지. 바인드: inst_id, sid
-- id: session.open_cursors
-- dbms: tibero
-- min-version: 6
-- requires: v$open_cursor
-- license: none
SELECT c.sql_id, CAST(NULL AS VARCHAR2(64)) AS cursor_type, CAST(NULL AS DATE) AS last_sql_active_time, c.sql_text
  FROM v$open_cursor c
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND c.sid = :sid
 ORDER BY c.cursor_id DESC, c.sql_id
 FETCH FIRST 200 ROWS ONLY
