-- 세션 한 개(상세 머리, Tibero). 열 순서는 session.list 의 0~22 와 같다(값 바꿈 규칙도 같다). 바인드: inst_id, sid, serial
-- id: session.detail
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$event_name, v$waiter_session
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, s.sid, s.serial#,
       DECODE(s.status, 'RUNNING', 'ACTIVE', 'ASSIGNED', 'ACTIVE', 'READY', 'INACTIVE', s.status) AS status,
       s.username, s.osuser, s.machine, s.prog_name AS program, s.module, s.action,
       s.client_identifier, s.sql_id, s.sql_child_number, s.prev_sql_id,
       CASE WHEN e.name IS NOT NULL THEN e.name WHEN s.state = 'RECV_WAITING' THEN 'RECV_WAITING' END AS event,
       CASE WHEN e.class IS NULL AND s.state = 'RECV_WAITING' THEN 'Idle'
            ELSE DECODE(e.class, 'STAT_CLASS_IDLE', 'Idle', 'STAT_CLASS_APPLICATION', 'Application',
                        'STAT_CLASS_CONCURRENCY', 'Concurrency', 'STAT_CLASS_USER_IO', 'User I/O',
                        'STAT_CLASS_SYSTEM_IO', 'System I/O', 'STAT_CLASS_CLUSTER', 'Cluster', 'STAT_CLASS_CACHE', 'Cache',
                        'STAT_CLASS_SQL', 'SQL', 'STAT_CLASS_OTHER', 'Other', e.class) END AS wait_class,
       CASE WHEN s.wait_event >= 0 THEN TRUNC(s.wait_time / 10) ELSE 0 END AS seconds_in_wait,
       CASE WHEN s.status IN ('RUNNING', 'ASSIGNED') THEN TRUNC(s.sql_et / 1000) ELSE TRUNC(s.wait_time / 10) END AS last_call_et,
       CASE WHEN b.hold_sid IS NOT NULL THEN TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) END AS blocking_instance,
       b.hold_sid AS blocking_session, s.logon_time,
       CASE WHEN s.type = 'WTHR' THEN 'USER' ELSE 'BACKGROUND' END AS type, s.prev_child_number
  FROM v$session s
  LEFT JOIN v$event_name e ON e.event# = s.wait_event
  LEFT JOIN (SELECT w.wait_sid, MIN(w.hold_sid) AS hold_sid FROM v$waiter_session w GROUP BY w.wait_sid) b
    ON b.wait_sid = s.sid
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND s.sid = :sid AND s.serial# = :serial
