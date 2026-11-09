-- ASH 대기 분해(Tibero V$ACTIVE_SESSION_HISTORY): sql_id 의 표본을 대기 이벤트로 묶는다. 열은 Oracle tuning.ash_waits 와 같다.
-- Tibero 에 SESSION_STATE·EVENT·WAIT_CLASS 열이 없다: WAIT_EVENT 번호가 음수(대기 아님)면 'ON CPU', 아니면 V$EVENT_NAME 이름·분류.
-- Tibero 는 관리 팩이 없어 license: none. 시험 서버는 표본 0행(ASH 수집 꺼짐 추정). 바인드: sql_id, since
-- id: tuning.ash_waits
-- dbms: tibero
-- min-version: 6
-- requires: v$active_session_history, v$event_name
-- license: none
SELECT x.event, x.wait_class, COUNT(*) AS samples
  FROM (SELECT CASE WHEN a.wait_event < 0 OR e.name IS NULL THEN 'ON CPU' ELSE e.name END AS event,
               CASE WHEN a.wait_event < 0 OR e.name IS NULL THEN NULL
                    ELSE DECODE(e.class, 'STAT_CLASS_IDLE', 'Idle', 'STAT_CLASS_APPLICATION', 'Application',
                                'STAT_CLASS_CONCURRENCY', 'Concurrency', 'STAT_CLASS_USER_IO', 'User I/O',
                                'STAT_CLASS_SYSTEM_IO', 'System I/O', 'STAT_CLASS_CLUSTER', 'Cluster', 'STAT_CLASS_CACHE', 'Cache',
                                'STAT_CLASS_SQL', 'SQL', 'STAT_CLASS_OTHER', 'Other', e.class) END AS wait_class
          FROM v$active_session_history a
          LEFT JOIN v$event_name e ON e.event# = a.wait_event
         WHERE a.sql_id = :sql_id
           AND a.sample_time >= :since) x
 GROUP BY x.event, x.wait_class
 ORDER BY samples DESC, x.event
