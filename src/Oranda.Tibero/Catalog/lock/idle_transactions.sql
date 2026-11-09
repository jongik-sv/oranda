-- 방치 트랜잭션(Tibero). 트랜잭션을 연 채 쉬는(READY) 세션, 쉰 초(WAIT_TIME/10) 내림차순(최대 500). 열 순서는 Oracle lock.idle_transactions 와 같다.
-- 세션과 트랜잭션은 V$TRANSACTION.SESS_ID 로 잇는다. start_date 는 서버 시간대로 UTC 로 바꾼다. blocked_count = V$WAITER_SESSION 의 직접 대기자 수,
-- lock_count = 잡은 WLOCK_DML·WLOCK_TX 수(Oracle TM·TX). 바인드: min_idle(초)
-- id: lock.idle_transactions
-- dbms: tibero
-- min-version: 6
-- requires: v$transaction, v$session, v$lock, v$waiter_session
-- license: none
SELECT inst_id, sid, serial#, username, machine, program, start_date, last_call_et, used_ublk, blocked_count, prev_sql_id, lock_count
  FROM (SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, s.sid, s.serial#, s.username, s.machine,
               s.prog_name AS program,
               SYS_EXTRACT_UTC(FROM_TZ(CAST(t.start_time AS TIMESTAMP), TO_CHAR(SYSTIMESTAMP, 'TZH:TZM'))) AS start_date,
               TRUNC(s.wait_time / 10) AS last_call_et, t.used_blk AS used_ublk,
               (SELECT COUNT(DISTINCT w.wait_sid) FROM v$waiter_session w WHERE w.hold_sid = s.sid) AS blocked_count,
               s.prev_sql_id,
               (SELECT COUNT(*) FROM v$lock l WHERE l.sess_id = s.sid AND l.type IN ('WLOCK_DML', 'WLOCK_TX')) AS lock_count
          FROM v$transaction t
          JOIN v$session s ON s.sid = t.sess_id
         WHERE s.status = 'READY'
           AND s.type = 'WTHR'
           AND TRUNC(s.wait_time / 10) >= :min_idle
         ORDER BY s.wait_time DESC, s.sid)
 WHERE ROWNUM <= 500
