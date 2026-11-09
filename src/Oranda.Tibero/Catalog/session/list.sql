-- 세션 목록(세션 모니터 기본 격자, Tibero). 열 순서·바인드는 Oracle session.list 와 같다(0~22 = session.detail, 23~27 = 통계).
-- 바인드: username(null=전체), active_only(0/1), include_bg(0/1), text(null=전체)
-- Tibero 차이(docs/design/tibero-monitor-catalog.md): status RUNNING·ASSIGNED/READY → ACTIVE/INACTIVE, type WTHR → USER(그 밖 BACKGROUND),
-- event = V$EVENT_NAME.NAME(WAIT_EVENT 번호), 클라이언트 대기(RECV_WAITING)는 event RECV_WAITING·wait_class Idle,
-- seconds_in_wait = WAIT_TIME/10(실측 약 1/10초), last_call_et = 실행 중 SQL_ET/1000(밀리초) · 쉬는 중 WAIT_TIME/10,
-- 차단자 = V$WAITER_SESSION.HOLD_SID, inst_id = USERENV INSTANCE(단일 인스턴스, TAC 는 나중).
-- 통계: cpu = 'DB CPU time'/10000(마이크로초 → 1/100초, Oracle 'CPU used by this session' 단위), logical_reads = 'logical reads', redo_size = 'redo log size'.
-- id: session.list
-- dbms: tibero
-- min-version: 6
-- requires: v$session, v$sesstat, v$event_name, v$waiter_session
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, s.sid, s.serial#,
       DECODE(s.status, 'RUNNING', 'ACTIVE', 'ASSIGNED', 'ACTIVE', 'READY', 'INACTIVE', s.status) AS status,
       s.username, s.osuser, s.machine, s.prog_name AS program, s.module, s.action,
       s.client_identifier, s.sql_id, s.sql_child_number, s.prev_sql_id,
       CASE WHEN e.name IS NOT NULL THEN e.name WHEN s.state = 'RECV_WAITING' THEN 'RECV_WAITING' END AS event,
       CASE WHEN e.class IS NULL AND s.state = 'RECV_WAITING' THEN 'Idle'
            ELSE DECODE(e.class, 'STAT_CLASS_IDLE', 'Idle', 'STAT_CLASS_APPLICATION', 'Application',
                        'STAT_CLASS_CONCURRENCY', 'Concurrency', 'STAT_CLASS_USER_IO', 'User I/O',
                        'STAT_CLASS_SYSTEM_IO', 'System I/O', 'STAT_CLASS_CLUSTER', 'Cluster', 'STAT_CLASS_CACHE', 'Cache',
                        'STAT_CLASS_SQL', 'SQL', 'STAT_CLASS_OTHER', 'Other', e.class) END AS wait_class,
       CASE WHEN s.wait_event >= 0 THEN TRUNC(s.wait_time / 10) ELSE 0 END AS seconds_in_wait,
       CASE WHEN s.status IN ('RUNNING', 'ASSIGNED') THEN TRUNC(s.sql_et / 1000) ELSE TRUNC(s.wait_time / 10) END AS last_call_et,
       CASE WHEN b.hold_sid IS NOT NULL THEN TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) END AS blocking_instance,
       b.hold_sid AS blocking_session, s.logon_time,
       CASE WHEN s.type = 'WTHR' THEN 'USER' ELSE 'BACKGROUND' END AS type, s.prev_child_number,
       st.cpu, st.logical_reads, st.physical_reads, st.executions, st.redo_size
  FROM v$session s
  LEFT JOIN v$event_name e ON e.event# = s.wait_event
  LEFT JOIN (SELECT w.wait_sid, MIN(w.hold_sid) AS hold_sid FROM v$waiter_session w GROUP BY w.wait_sid) b
    ON b.wait_sid = s.sid
  LEFT JOIN (SELECT ss.sid,
                    TRUNC(SUM(CASE ss.name WHEN 'DB CPU time' THEN ss.value END) / 10000) AS cpu,
                    SUM(CASE ss.name WHEN 'logical reads' THEN ss.value END) AS logical_reads,
                    SUM(CASE ss.name WHEN 'physical reads' THEN ss.value END) AS physical_reads,
                    SUM(CASE ss.name WHEN 'execute count' THEN ss.value END) AS executions,
                    SUM(CASE ss.name WHEN 'redo log size' THEN ss.value END) AS redo_size
               FROM v$sesstat ss
              WHERE ss.name IN ('DB CPU time', 'logical reads', 'physical reads', 'execute count', 'redo log size')
              GROUP BY ss.sid) st
    ON st.sid = s.sid
 WHERE (:username IS NULL OR s.username = :username OR s.username = UPPER(:username))
   AND (:active_only = 0 OR s.status IN ('RUNNING', 'ASSIGNED'))
   AND (:include_bg = 1 OR s.type = 'WTHR')
   AND (:text IS NULL
        OR INSTR(UPPER(s.username || '|' || s.osuser || '|' || s.machine || '|' || s.prog_name || '|'
                       || s.module || '|' || s.sql_id), UPPER(:text)) > 0)
 ORDER BY s.sid
