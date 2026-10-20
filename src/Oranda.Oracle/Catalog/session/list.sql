-- 세션 목록(세션 모니터 기본 격자). 델타용 누적 통계 5종을 같은 행에 붙인다.
-- 바인드: username(null=전체), active_only(0/1), include_bg(0/1), text(null=전체, 사용자·머신·프로그램·모듈·SQL ID 부분 문자열)
-- 열 순서는 OracleMonitorService 의 행 매핑과 같다(0~22 = session.detail 과 같음, 23~27 = 통계).
-- id: session.list
-- min-version: 11
-- requires: gv$session, gv$sesstat, gv$statname
-- license: none
-- rac: gv
SELECT s.inst_id, s.sid, s.serial#, s.status, s.username, s.osuser, s.machine, s.program, s.module, s.action,
       s.client_identifier, s.sql_id, s.sql_child_number, s.prev_sql_id, s.event, s.wait_class,
       CASE WHEN s.state = 'WAITING' THEN TRUNC(s.wait_time_micro / 1000000) ELSE 0 END AS seconds_in_wait,
       s.last_call_et, s.blocking_instance, s.blocking_session, s.logon_time, s.type, s.prev_child_number,
       st.cpu, st.logical_reads, st.physical_reads, st.executions, st.redo_size
  FROM gv$session s
  LEFT JOIN (SELECT ss.inst_id, ss.sid,
                    SUM(CASE n.name WHEN 'CPU used by this session' THEN ss.value END) AS cpu,
                    SUM(CASE n.name WHEN 'session logical reads' THEN ss.value END) AS logical_reads,
                    SUM(CASE n.name WHEN 'physical reads' THEN ss.value END) AS physical_reads,
                    SUM(CASE n.name WHEN 'execute count' THEN ss.value END) AS executions,
                    SUM(CASE n.name WHEN 'redo size' THEN ss.value END) AS redo_size
               FROM gv$sesstat ss
               JOIN gv$statname n ON n.inst_id = ss.inst_id AND n.statistic# = ss.statistic#
              WHERE n.name IN ('CPU used by this session', 'session logical reads', 'physical reads',
                               'execute count', 'redo size')
              GROUP BY ss.inst_id, ss.sid) st
    ON st.inst_id = s.inst_id AND st.sid = s.sid
 WHERE (:username IS NULL OR s.username = :username OR s.username = UPPER(:username))
   AND (:active_only = 0 OR s.status = 'ACTIVE')
   AND (:include_bg = 1 OR s.type <> 'BACKGROUND')
   AND (:text IS NULL
        OR INSTR(UPPER(s.username || '|' || s.osuser || '|' || s.machine || '|' || s.program || '|'
                       || s.module || '|' || s.sql_id), UPPER(:text)) > 0)
 ORDER BY s.inst_id, s.sid
