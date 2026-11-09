-- sql_id 의 누적 실행 통계(Tibero V$SQL, 자식 커서 합). 열은 Oracle tuning.sql_stats 와 같다. 공유 풀에 없으면 children = 0.
-- PARSING_SCHEMA_NAME 이 없어 PARSING_SCHEMA_ID 를 ALL_USERS 로 이름으로 바꾼다. 단일 인스턴스라 instances = 0 또는 1. 바인드: sql_id
-- id: tuning.sql_stats
-- dbms: tibero
-- min-version: 6
-- requires: v$sql, all_users
-- license: none
SELECT a.children, a.instances, a.executions, a.elapsed_time, a.cpu_time, a.buffer_gets, a.disk_reads, a.rows_processed,
       a.application_wait_time, a.concurrency_wait_time, a.cluster_wait_time, a.user_io_wait_time, a.parse_calls, a.fetches,
       (SELECT u.username FROM all_users u WHERE u.user_id = a.parsing_schema_id) AS parsing_schema_name,
       a.module, a.last_active_time, a.first_load_time
  FROM (SELECT COUNT(*) AS children, CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS instances,
               SUM(q.executions) AS executions, SUM(q.elapsed_time) AS elapsed_time, SUM(q.cpu_time) AS cpu_time,
               SUM(q.buffer_gets) AS buffer_gets, SUM(q.disk_reads) AS disk_reads, SUM(q.rows_processed) AS rows_processed,
               SUM(q.application_wait_time) AS application_wait_time, SUM(q.concurrency_wait_time) AS concurrency_wait_time,
               SUM(q.cluster_wait_time) AS cluster_wait_time, SUM(q.user_io_wait_time) AS user_io_wait_time,
               SUM(q.parse_calls) AS parse_calls, SUM(q.fetches) AS fetches,
               MAX(q.parsing_schema_id) AS parsing_schema_id, MAX(q.module) AS module,
               MAX(q.last_active_time) AS last_active_time, MIN(q.first_load_time) AS first_load_time
          FROM v$sql q
         WHERE q.sql_id = :sql_id) a
