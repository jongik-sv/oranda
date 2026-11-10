-- 복사 대기 원인(WH1): 복사 세션 하나의 대기 이벤트와 그 세션을 막는 세션. 감시용 짧은 세션이 읽는다(복사 세션은 명령 중이라 자기 V$SESSION 을 못 읽는다).
-- 열 = EVENT, WAIT_CLASS, 대기 초, 막는 SID, 막는 쪽 USERNAME·PROGRAM·MACHINE. 바인드: SID(복사 세션 SID), INST(복사 세션 인스턴스 이름, NULL = 감시 세션의 인스턴스).
-- 다른 인스턴스(RAC)의 같은 SID 를 잘못 읽지 않게 인스턴스 이름으로 고른다. 세션이 없으면 0행.
-- id: copy.wait_cause
-- min-version: 11
-- requires: gv$session, gv$instance
-- license: none
SELECT s.EVENT, s.WAIT_CLASS, CASE WHEN s.STATE = 'WAITING' THEN TRUNC(s.WAIT_TIME_MICRO / 1000000) ELSE 0 END AS SECONDS_IN_WAIT, s.BLOCKING_SESSION, b.USERNAME, b.PROGRAM, b.MACHINE FROM GV$SESSION s JOIN GV$INSTANCE i ON i.INST_ID = s.INST_ID LEFT JOIN GV$SESSION b ON b.INST_ID = s.BLOCKING_INSTANCE AND b.SID = s.BLOCKING_SESSION WHERE s.SID = :SID AND i.INSTANCE_NAME = NVL(:INST, SYS_CONTEXT('USERENV', 'INSTANCE_NAME'))
