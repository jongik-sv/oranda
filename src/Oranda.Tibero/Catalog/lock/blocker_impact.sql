-- 보유자 영향 순위(Tibero). 직접 보유자(V$WAITER_SESSION.HOLD_SID)와 최종 보유자(보유자를 따라 올라간 뿌리) 모두에게 대기자를 센다(같은 대기자는 한 번).
-- 대기 초 = 대기자의 WAIT_TIME/10(대기 중일 때). 합계 대기 초 내림차순. 열 순서는 Oracle lock.blocker_impact 와 같다. 마지막 열 = 보유자 status. 바인드 없음.
-- id: lock.blocker_impact
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$waiter_session
-- license: none
SELECT x.holder_inst, x.holder_sid, h.serial#, COUNT(*) AS waiter_count,
       MAX(x.wait_secs) AS max_wait_secs, SUM(x.wait_secs) AS total_wait_secs,
       DECODE(h.status, 'RUNNING', 'ACTIVE', 'READY', 'INACTIVE', h.status) AS status
  FROM (SELECT p.waiter, TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS holder_inst, p.holder AS holder_sid,
               CASE WHEN q.wait_event >= 0 THEN TRUNC(q.wait_time / 10) ELSE 0 END AS wait_secs
          FROM (SELECT w.wait_sid AS waiter, w.hold_sid AS holder
                  FROM v$waiter_session w
                UNION
                SELECT CONNECT_BY_ROOT w.wait_sid, w.hold_sid
                  FROM v$waiter_session w
                 WHERE CONNECT_BY_ISLEAF = 1
               CONNECT BY NOCYCLE PRIOR w.hold_sid = w.wait_sid) p
          JOIN v$session q ON q.sid = p.waiter) x
  JOIN v$session h ON h.sid = x.holder_sid
 WHERE x.waiter <> x.holder_sid
 GROUP BY x.holder_inst, x.holder_sid, h.serial#, h.status
 ORDER BY total_wait_secs DESC, waiter_count DESC, x.holder_inst, x.holder_sid
