-- 세션의 진행 중인 긴 작업(Tibero V$SESSION_LONGOPS). 열 순서는 Oracle session.longops 와 같다.
-- Tibero 에 없는 열: TIME_REMAINING·MESSAGE → NULL. elapsed_seconds = ELAPSED_TIME(단위 미확인, 진행 중인 긴 작업이 있을 때 확인). 바인드: inst_id, sid, serial
-- id: session.longops
-- dbms: tibero
-- min-version: 6
-- requires: v$session_longops
-- license: none
SELECT l.opname, l.target, ROUND(l.sofar / NULLIF(l.totalwork, 0) * 100, 1) AS pct_done,
       l.sofar, l.totalwork, l.units, l.elapsed_time AS elapsed_seconds, CAST(NULL AS NUMBER) AS time_remaining,
       l.start_time, CAST(NULL AS VARCHAR2(512)) AS message
  FROM v$session_longops l
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND l.sid = :sid AND l.serial# = :serial
   AND l.totalwork > 0 AND l.sofar < l.totalwork
 ORDER BY l.start_time DESC
