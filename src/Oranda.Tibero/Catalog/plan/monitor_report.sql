-- SQL 모니터 보고서 텍스트(Tibero DBMS_SQLTUNE.REPORT_SQL_MONITOR(SQL_ID, SESSION_ID, SESSION_SERIAL, SQL_EXEC_START, SQL_EXEC_ID) → CLOB).
-- Tibero 는 관리 팩이 없어 license: none. 형식 인수(type)가 없어 텍스트만 나온다(HTML 판 plan.monitor_report_html 은 Tibero 에 없음). 바인드: sql_id
-- id: plan.monitor_report
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_monitor
-- license: none
SELECT DBMS_SQLTUNE.REPORT_SQL_MONITOR(:sql_id) AS report
  FROM dual
