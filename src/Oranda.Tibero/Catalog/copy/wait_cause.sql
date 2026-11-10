-- 복사 대기 원인(WH1, Tibero): 복사 세션 하나의 대기 이벤트와 그 세션을 막는 세션. 열·바인드는 Oracle 판과 같다.
-- 대기 이벤트·대기 종류·대기 초는 session.detail(Tibero)과 같은 계산, 막는 세션은 V$WAITER_SESSION(HOLD_SID). V$SESSION 은 이 인스턴스만 보므로
-- INST 가 이 인스턴스가 아니면 0행이다(다른 인스턴스의 같은 SID 를 읽지 않는다). 바인드: SID, INST(NULL = 이 인스턴스).
-- id: copy.wait_cause
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$event_name, v$waiter_session
-- license: none
SELECT CASE WHEN e.NAME IS NOT NULL THEN e.NAME WHEN s.STATE = 'RECV_WAITING' THEN 'RECV_WAITING' END AS EVENT, DECODE(e.CLASS, 'STAT_CLASS_IDLE', 'Idle', 'STAT_CLASS_APPLICATION', 'Application', 'STAT_CLASS_CONCURRENCY', 'Concurrency', 'STAT_CLASS_USER_IO', 'User I/O', 'STAT_CLASS_SYSTEM_IO', 'System I/O', 'STAT_CLASS_CLUSTER', 'Cluster', 'STAT_CLASS_CACHE', 'Cache', 'STAT_CLASS_SQL', 'SQL', 'STAT_CLASS_OTHER', 'Other', e.CLASS) AS WAIT_CLASS, CASE WHEN s.WAIT_EVENT >= 0 THEN TRUNC(s.WAIT_TIME / 10) ELSE 0 END AS SECONDS_IN_WAIT, b.HOLD_SID AS BLOCKING_SESSION, h.USERNAME, h.PROG_NAME AS PROGRAM, h.MACHINE FROM V$SESSION s LEFT JOIN V$EVENT_NAME e ON e.EVENT# = s.WAIT_EVENT LEFT JOIN (SELECT w.WAIT_SID, MIN(w.HOLD_SID) AS HOLD_SID FROM V$WAITER_SESSION w GROUP BY w.WAIT_SID) b ON b.WAIT_SID = s.SID LEFT JOIN V$SESSION h ON h.SID = b.HOLD_SID WHERE s.SID = :SID AND NVL(:INST, SYS_CONTEXT('USERENV', 'INSTANCE_NAME')) = SYS_CONTEXT('USERENV', 'INSTANCE_NAME')
