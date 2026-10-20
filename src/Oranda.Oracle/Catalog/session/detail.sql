-- 세션 한 개(상세 머리). 열 순서는 session.list 의 0~22 와 같다. 바인드: inst_id, sid, serial
-- id: session.detail
-- min-version: 11
-- requires: gv$session
-- license: none
-- rac: gv
SELECT s.inst_id, s.sid, s.serial#, s.status, s.username, s.osuser, s.machine, s.program, s.module, s.action,
       s.client_identifier, s.sql_id, s.sql_child_number, s.prev_sql_id, s.event, s.wait_class,
       CASE WHEN s.state = 'WAITING' THEN TRUNC(s.wait_time_micro / 1000000) ELSE 0 END AS seconds_in_wait,
       s.last_call_et, s.blocking_instance, s.blocking_session, s.logon_time, s.type, s.prev_child_number
  FROM gv$session s
 WHERE s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial
