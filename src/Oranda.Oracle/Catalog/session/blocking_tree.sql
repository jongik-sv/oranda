-- 블로킹 트리 재료: 대기자(blocking_session 있음)와 그 보유자. final_blocking_* 는 뿌리 확인용.
-- 대기자 행의 locked_object 는 기다리는 행의 객체(row_wait_obj#). @PFX@ = dba 또는 all.
-- id: session.blocking_tree
-- min-version: 11
-- requires: gv$session, dba_objects, all_objects
-- license: none
-- rac: gv
SELECT s.inst_id, s.sid, s.serial#, s.username, s.event,
       CASE WHEN s.state = 'WAITING' THEN TRUNC(s.wait_time_micro / 1000000) ELSE 0 END AS seconds_in_wait,
       s.sql_id, s.blocking_instance, s.blocking_session, s.final_blocking_instance, s.final_blocking_session,
       CASE WHEN s.blocking_session IS NOT NULL AND s.row_wait_obj# > 0 THEN
            (SELECT o.owner || '.' || o.object_name FROM @PFX@_objects o WHERE o.object_id = s.row_wait_obj#)
       END AS locked_object
  FROM gv$session s
 WHERE s.blocking_session IS NOT NULL
    OR (s.inst_id, s.sid) IN (SELECT w.blocking_instance, w.blocking_session
                                FROM gv$session w
                               WHERE w.blocking_session IS NOT NULL)
 ORDER BY s.inst_id, s.sid
