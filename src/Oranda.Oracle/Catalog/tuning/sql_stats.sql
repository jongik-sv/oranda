-- sql_id 의 누적 실행 통계(TN-17 보고서): 모든 인스턴스·자식 커서 합. 공유 풀에 없으면 children = 0. 바인드: sql_id
-- id: tuning.sql_stats
-- min-version: 11
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT COUNT(*) AS children, COUNT(DISTINCT q.inst_id) AS instances,
       SUM(q.executions) AS executions, SUM(q.elapsed_time) AS elapsed_time, SUM(q.cpu_time) AS cpu_time,
       SUM(q.buffer_gets) AS buffer_gets, SUM(q.disk_reads) AS disk_reads, SUM(q.rows_processed) AS rows_processed,
       SUM(q.application_wait_time) AS application_wait_time, SUM(q.concurrency_wait_time) AS concurrency_wait_time,
       SUM(q.cluster_wait_time) AS cluster_wait_time, SUM(q.user_io_wait_time) AS user_io_wait_time,
       SUM(q.parse_calls) AS parse_calls, SUM(q.fetches) AS fetches,
       MAX(q.parsing_schema_name) AS parsing_schema_name, MAX(q.module) AS module,
       MAX(q.last_active_time) AS last_active_time, MIN(q.first_load_time) AS first_load_time
  FROM gv$sql q
 WHERE q.sql_id = :sql_id
