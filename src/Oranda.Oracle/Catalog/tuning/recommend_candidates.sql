-- 튜닝 추천 후보(TSK-14-17, D-051): gv$sqlstats 에서 누적 경과 시간 상위 :top 개만 읽고, 고른 그 행에만 파싱 스키마(gv$sql)를 붙인다. 팩 불필요.
-- gv$sqlstats 는 sql_id·plan_hash 단위 누적 통계(공유 풀 잠금 없이 읽는 뷰)라 gv$sqlarea·gv$sql 전체 집계보다 가볍다.
-- 바인드: top(후보 수), min_executions(실행 횟수 하한), sql_ids(쉼표로 이은 sql_id 목록 — null 이면 전체)
-- 열 순서는 OracleTuningService.Recommend 의 행 매핑과 같다. total_elapsed = 조건에 맞는 모든 행의 경과 시간 합(비중 계산용).
-- id: tuning.recommend_candidates
-- min-version: 11
-- requires: gv$sqlstats, gv$sql
-- license: none
-- rac: gv
SELECT t.inst_id, t.sql_id, t.plan_hash_value, t.sql_text, t.executions, t.elapsed_time, t.cpu_time, t.buffer_gets,
       t.disk_reads, t.rows_processed, t.parse_calls, t.version_count, t.fms,
       t.user_io_wait_time, t.application_wait_time, t.concurrency_wait_time, t.cluster_wait_time, t.last_active_time,
       t.total_elapsed,
       (SELECT MAX(q.parsing_schema_name) FROM gv$sql q WHERE q.inst_id = t.inst_id AND q.sql_id = t.sql_id) AS parsing_schema_name
  FROM (SELECT s.inst_id, s.sql_id, s.plan_hash_value, s.sql_text, s.executions, s.elapsed_time, s.cpu_time, s.buffer_gets,
               s.disk_reads, s.rows_processed, s.parse_calls, s.version_count, TO_CHAR(s.force_matching_signature) AS fms,
               s.user_io_wait_time, s.application_wait_time, s.concurrency_wait_time, s.cluster_wait_time, s.last_active_time,
               SUM(s.elapsed_time) OVER () AS total_elapsed
          FROM gv$sqlstats s
         WHERE s.executions >= :min_executions
           AND (:sql_ids IS NULL OR INSTR(',' || :sql_ids || ',', ',' || s.sql_id || ',') > 0)
         ORDER BY s.elapsed_time DESC, s.sql_id, s.inst_id) t
 WHERE ROWNUM <= :top
