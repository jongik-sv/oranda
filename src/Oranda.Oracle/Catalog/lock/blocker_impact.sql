-- 보유자 영향 순위(LK-07). 직접 보유자(blocking_session)와 최종 보유자(final_blocking_session) 모두에게 대기자를 센다(같은 대기자는 한 번).
-- 대기 초 = 대기 중이면 wait_time_micro. 합계 대기 초 내림차순. 바인드 없음.
-- id: lock.blocker_impact
-- min-version: 11
-- requires: gv$session
-- license: none
-- rac: gv
SELECT x.holder_inst, x.holder_sid, h.serial#, COUNT(*) AS waiter_count,
       MAX(x.wait_secs) AS max_wait_secs, SUM(x.wait_secs) AS total_wait_secs
  FROM (SELECT w.inst_id, w.sid, w.blocking_instance AS holder_inst, w.blocking_session AS holder_sid,
               CASE WHEN w.state = 'WAITING' THEN TRUNC(w.wait_time_micro / 1000000) ELSE 0 END AS wait_secs
          FROM gv$session w
         WHERE w.blocking_session IS NOT NULL
        UNION
        SELECT w.inst_id, w.sid, w.final_blocking_instance, w.final_blocking_session,
               CASE WHEN w.state = 'WAITING' THEN TRUNC(w.wait_time_micro / 1000000) ELSE 0 END
          FROM gv$session w
         WHERE w.final_blocking_session IS NOT NULL) x
  JOIN gv$session h ON h.inst_id = x.holder_inst AND h.sid = x.holder_sid
 WHERE NOT (x.inst_id = x.holder_inst AND x.sid = x.holder_sid)
 GROUP BY x.holder_inst, x.holder_sid, h.serial#
 ORDER BY total_wait_secs DESC, waiter_count DESC, x.holder_inst, x.holder_sid
