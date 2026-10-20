-- 세션의 진행 중인 긴 작업(gv$session_longops, 팩 불필요). 최근 시작한 것부터. 바인드: inst_id, sid, serial
-- id: session.longops
-- min-version: 11
-- requires: gv$session_longops
-- license: none
-- rac: gv
SELECT l.opname, l.target, ROUND(l.sofar / NULLIF(l.totalwork, 0) * 100, 1) AS pct_done,
       l.sofar, l.totalwork, l.units, l.elapsed_seconds, l.time_remaining, l.start_time, l.message
  FROM gv$session_longops l
 WHERE l.inst_id = :inst_id AND l.sid = :sid AND l.serial# = :serial
   AND l.totalwork > 0 AND l.sofar < l.totalwork
 ORDER BY l.start_time DESC
