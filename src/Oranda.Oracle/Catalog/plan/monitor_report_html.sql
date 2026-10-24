-- SQL 모니터 보고서 HTML(외부 브라우저로 연다, TSK-06-04). Tuning Pack 필요 — 보유 선언·서버 설정이 모자라면 보내지 않는다. 바인드: sql_id
-- id: plan.monitor_report_html
-- min-version: 11
-- requires: v$sql_monitor
-- license: tuning
SELECT DBMS_SQLTUNE.REPORT_SQL_MONITOR(sql_id => :sql_id, type => 'HTML') AS report
  FROM dual
