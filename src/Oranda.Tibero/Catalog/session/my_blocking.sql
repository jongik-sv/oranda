-- 내 세션이 남을 막고 있는지(Tibero): 막은 세션 수(직접 대기자, V$WAITER_SESSION), 내 트랜잭션 경과 초(V$TRANSACTION.SESS_ID),
-- 내 마지막 호출 후 경과 초(session.list 의 last_call_et 와 같은 계산), MAX_IDLE_BLOCKER_TIME(Tibero 에 해당 매개변수 없음 → NULL).
-- 트랜잭션·세션이 없어도 항상 한 행. 바인드: sid
-- id: session.my_blocking
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$transaction, v$waiter_session
-- license: none
SELECT (SELECT COUNT(DISTINCT w.wait_sid) FROM v$waiter_session w WHERE w.hold_sid = :sid) AS blocked_count,
       (SELECT TRUNC((SYSDATE - MIN(t.start_time)) * 86400) FROM v$transaction t WHERE t.sess_id = :sid) AS txn_age_seconds,
       (SELECT MAX(CASE WHEN s.status IN ('RUNNING', 'ASSIGNED') THEN TRUNC(s.sql_et / 1000) ELSE TRUNC(s.wait_time / 10) END)
          FROM v$session s WHERE s.sid = :sid) AS last_call_et,
       CAST(NULL AS NUMBER) AS max_idle_blocker_time
  FROM dual
