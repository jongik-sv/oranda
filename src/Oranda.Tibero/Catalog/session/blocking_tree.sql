-- 블로킹 트리 재료(Tibero): 대기자(V$WAITER_SESSION 의 WAIT_SID)와 그 보유자. 열 순서는 Oracle session.blocking_tree 와 같다.
-- blocking_session = 직접 보유자(여럿이면 가장 작은 SID), final_blocking_session = 보유자를 따라 올라간 뿌리(V$SESSION 에 그 열이 없어 계산).
-- 대기자 행의 locked_object 는 기다리는 행의 객체(ROW_WAIT_OBJ_ID). 마지막 열 = status(RUNNING·ASSIGNED/READY → ACTIVE/INACTIVE). @PFX@ = dba 또는 all.
-- id: session.blocking_tree
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$waiter_session, v$event_name, dba_objects, all_objects
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, s.sid, s.serial#, s.username,
       CASE WHEN e.name IS NOT NULL THEN e.name WHEN s.state = 'RECV_WAITING' THEN 'RECV_WAITING' END AS event,
       CASE WHEN s.wait_event >= 0 THEN TRUNC(s.wait_time / 10) ELSE 0 END AS seconds_in_wait,
       s.sql_id,
       CASE WHEN b.hold_sid IS NOT NULL THEN TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) END AS blocking_instance,
       b.hold_sid AS blocking_session,
       CASE WHEN b.hold_sid IS NOT NULL THEN TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) END AS final_blocking_instance,
       (SELECT r.root_sid
          FROM (SELECT CONNECT_BY_ROOT w.wait_sid AS waiter, w.hold_sid AS root_sid
                  FROM v$waiter_session w
                 WHERE CONNECT_BY_ISLEAF = 1
               CONNECT BY NOCYCLE PRIOR w.hold_sid = w.wait_sid) r
         WHERE r.waiter = s.sid AND ROWNUM = 1) AS final_blocking_session,
       CASE WHEN b.hold_sid IS NOT NULL AND s.row_wait_obj_id > 0 THEN
            (SELECT o.owner || '.' || o.object_name FROM @PFX@_objects o WHERE o.object_id = s.row_wait_obj_id)
       END AS locked_object,
       DECODE(s.status, 'RUNNING', 'ACTIVE', 'ASSIGNED', 'ACTIVE', 'READY', 'INACTIVE', s.status) AS status
  FROM v$session s
  LEFT JOIN v$event_name e ON e.event# = s.wait_event
  LEFT JOIN (SELECT w.wait_sid, MIN(w.hold_sid) AS hold_sid FROM v$waiter_session w GROUP BY w.wait_sid) b
    ON b.wait_sid = s.sid
 WHERE s.sid IN (SELECT w.wait_sid FROM v$waiter_session w)
    OR s.sid IN (SELECT w.hold_sid FROM v$waiter_session w)
 ORDER BY s.sid
