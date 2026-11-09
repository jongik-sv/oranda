-- 대기 표본 한 번(Tibero V$SESSION). 열은 Oracle tuning.wait_sample 와 같다. 대기 중이 아니면(WAIT_EVENT 음수) event·wait_class 는 null.
-- 내 세션 = V$MYSTAT 의 SID(USERENV 'SID' 는 TBR-11006). 실행 중 = STATUS RUNNING(Oracle ACTIVE). 바인드: sql_id(null = 내 세션)
-- id: tuning.wait_sample
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$event_name, v$mystat
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, s.sid, s.sql_id,
       CASE WHEN s.wait_event >= 0 THEN e.name END AS event,
       CASE WHEN s.wait_event >= 0 THEN
            DECODE(e.class, 'STAT_CLASS_IDLE', 'Idle', 'STAT_CLASS_APPLICATION', 'Application',
                   'STAT_CLASS_CONCURRENCY', 'Concurrency', 'STAT_CLASS_USER_IO', 'User I/O',
                   'STAT_CLASS_SYSTEM_IO', 'System I/O', 'STAT_CLASS_CLUSTER', 'Cluster', 'STAT_CLASS_CACHE', 'Cache',
                   'STAT_CLASS_SQL', 'SQL', 'STAT_CLASS_OTHER', 'Other', e.class) END AS wait_class
  FROM v$session s
  LEFT JOIN v$event_name e ON e.event# = s.wait_event
 WHERE (:sql_id IS NULL AND s.sid = (SELECT m.sid FROM v$mystat m WHERE ROWNUM = 1))
    OR (s.sql_id = :sql_id AND s.status = 'RUNNING')
 ORDER BY s.sid
