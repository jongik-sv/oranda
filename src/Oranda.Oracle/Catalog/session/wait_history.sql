-- 세션의 최근 대기 10건(gv$session_wait_history, 팩 불필요). seq# 1 이 가장 최근. 바인드: inst_id, sid
-- id: session.wait_history
-- min-version: 11
-- requires: gv$session_wait_history
-- license: none
-- rac: gv
SELECT w.seq#, w.event, w.wait_time_micro, w.time_since_last_wait_micro,
       CASE WHEN w.p1text IS NOT NULL THEN w.p1text || '=' || w.p1 END AS p1,
       CASE WHEN w.p2text IS NOT NULL THEN w.p2text || '=' || w.p2 END AS p2,
       CASE WHEN w.p3text IS NOT NULL THEN w.p3text || '=' || w.p3 END AS p3
  FROM gv$session_wait_history w
 WHERE w.inst_id = :inst_id AND w.sid = :sid
 ORDER BY w.seq#
