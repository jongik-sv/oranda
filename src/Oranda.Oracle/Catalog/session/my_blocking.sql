-- 내 세션이 남을 막고 있는지: 막은 세션 수, 내 트랜잭션 경과 초, 내 마지막 호출 후 경과 초, MAX_IDLE_BLOCKER_TIME(분). 바인드: sid
-- RAC 의 인스턴스·serial# 정밀 구분은 하지 않는다(SID 만으로 대조). 트랜잭션·세션이 없어도 항상 한 행.
-- id: session.my_blocking
-- min-version: 19
-- requires: gv$session, gv$transaction, v$parameter
-- license: none
-- rac: gv
SELECT (SELECT COUNT(*) FROM gv$session w
         WHERE w.blocking_session = :sid OR w.final_blocking_session = :sid) AS blocked_count,
       (SELECT TRUNC((SYSDATE - MIN(t.start_date)) * 86400)
          FROM gv$transaction t
          JOIN gv$session s ON s.inst_id = t.inst_id AND s.taddr = t.addr
         WHERE s.sid = :sid) AS txn_age_seconds,
       (SELECT MAX(s.last_call_et) FROM gv$session s WHERE s.sid = :sid) AS last_call_et,
       (SELECT TO_NUMBER(p.value) FROM v$parameter p WHERE p.name = 'max_idle_blocker_time') AS max_idle_blocker_time
  FROM dual
