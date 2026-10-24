-- 방치 트랜잭션(LK-12). 트랜잭션을 연 채 INACTIVE 인 세션, 마지막 호출 뒤 경과 초 내림차순(최대 500). 바인드: min_idle.
-- id: lock.idle_transactions
-- min-version: 11
-- requires: gv$transaction, gv$session
-- license: none
-- rac: gv
SELECT inst_id, sid, serial#, username, machine, program, start_date, last_call_et, used_ublk, blocked_count, prev_sql_id
  FROM (SELECT s.inst_id, s.sid, s.serial#, s.username, s.machine, s.program, t.start_date, s.last_call_et, t.used_ublk,
               (SELECT COUNT(*) FROM gv$session w
                 WHERE w.blocking_instance = s.inst_id AND w.blocking_session = s.sid) AS blocked_count,
               s.prev_sql_id
          FROM gv$transaction t
          JOIN gv$session s ON s.inst_id = t.inst_id AND s.taddr = t.addr
         WHERE s.status = 'INACTIVE'
           AND s.type <> 'BACKGROUND'
           AND s.last_call_et >= :min_idle
         ORDER BY s.last_call_et DESC, s.inst_id, s.sid)
 WHERE ROWNUM <= 500
