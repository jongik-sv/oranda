-- 대기 세션 SQL 의 캡처된 바인드(LK-12). sql_id 가 있으면 그 커서(inst_id·child 가 null 이면 전체), 없으면 세션(inst_id·sid·serial)의
-- 현재 SQL(없으면 직전 SQL)과 자식 번호. 열은 sql.binds 와 같다. 바인드: sql_id, child, inst_id, sid, serial.
-- id: lock.waiting_binds
-- min-version: 11
-- requires: gv$sql_bind_capture, gv$session
-- license: none
-- rac: gv
SELECT b.name, b.position, b.datatype_string, b.value_string, b.last_captured, b.was_captured
  FROM gv$sql_bind_capture b
  JOIN (SELECT :inst_id AS inst_id, :sql_id AS sql_id, :child AS child_number
          FROM dual
         WHERE :sql_id IS NOT NULL
        UNION ALL
        SELECT s.inst_id, NVL(s.sql_id, s.prev_sql_id),
               CASE WHEN s.sql_id IS NOT NULL THEN s.sql_child_number ELSE s.prev_child_number END
          FROM gv$session s
         WHERE :sql_id IS NULL AND s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial) t
    ON b.sql_id = t.sql_id
   AND (t.inst_id IS NULL OR b.inst_id = t.inst_id)
   AND (t.child_number IS NULL OR b.child_number = t.child_number)
 ORDER BY b.child_number, b.position
