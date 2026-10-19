-- SQL 모니터 보고서 텍스트. Tuning Pack 필요 — 보유 선언·서버 설정이 모자라면 보내지 않는다. 바인드: sql_id
-- id: plan.monitor_report
-- min-version: 19
-- requires: v$sql_monitor
-- license: tuning
SELECT DBMS_SQLTUNE.REPORT_SQL_MONITOR(sql_id => :sql_id, type => 'TEXT') AS report
  FROM dual
