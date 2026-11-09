-- 세션 통계(0 이 아닌 것, Tibero). V$SESSTAT 에 NAME 이 있어 V$STATNAME 과 합치지 않는다(Oracle 의 STATISTIC# 조인은 TBR-8026). 이름은 Tibero 이름 그대로.
-- 바인드: inst_id, sid
-- id: session.stats
-- dbms: tibero
-- min-version: 6
-- requires: v$sesstat
-- license: none
SELECT ss.name, ss.value
  FROM v$sesstat ss
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND ss.sid = :sid AND ss.value <> 0
 ORDER BY ss.name
