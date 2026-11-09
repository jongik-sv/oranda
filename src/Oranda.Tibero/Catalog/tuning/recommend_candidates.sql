-- 튜닝 추천 후보(Tibero): 누적 경과 시간 상위 :top 개. 열 순서는 Oracle tuning.recommend_candidates 와 같다.
-- Oracle 은 GV$SQLSTATS 를 쓰지만 Tibero V$SQLSTATS 는 ELAPSED_TIME·CPU_TIME 등이 음수가 부호 없는 64비트로 넘어간 값(18446744073…)을 돌려줘
-- 순위가 깨진다(실측) → V$SQL 을 sql_id·plan_hash_value 로 묶는다(V$SQLSTATS 와 같은 단위, V$SQL 값은 정상).
-- version_count = 묶음 안 자식 수, fms = NULL(FORCE_MATCHING_SIGNATURE 없음), 파싱 스키마 = PARSING_SCHEMA_ID → ALL_USERS, inst_id = USERENV INSTANCE.
-- 바인드: top, min_executions, sql_ids(쉼표로 이은 sql_id 목록 — null 이면 전체)
-- id: tuning.recommend_candidates
-- dbms: tibero
-- min-version: 6
-- requires: v$sql, all_users
-- license: none
SELECT t.inst_id, t.sql_id, t.plan_hash_value, t.sql_text, t.executions, t.elapsed_time, t.cpu_time, t.buffer_gets,
       t.disk_reads, t.rows_processed, t.parse_calls, t.version_count, t.fms,
       t.user_io_wait_time, t.application_wait_time, t.concurrency_wait_time, t.cluster_wait_time, t.last_active_time,
       t.total_elapsed,
       (SELECT u.username FROM all_users u WHERE u.user_id = t.parsing_schema_id) AS parsing_schema_name
  FROM (SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, g.*, SUM(g.elapsed_time) OVER () AS total_elapsed
          FROM (SELECT q.sql_id, q.plan_hash_value, MAX(q.sql_text) AS sql_text, SUM(q.executions) AS executions,
                       SUM(q.elapsed_time) AS elapsed_time, SUM(q.cpu_time) AS cpu_time, SUM(q.buffer_gets) AS buffer_gets,
                       SUM(q.disk_reads) AS disk_reads, SUM(q.rows_processed) AS rows_processed, SUM(q.parse_calls) AS parse_calls,
                       COUNT(*) AS version_count, CAST(NULL AS VARCHAR2(40)) AS fms,
                       SUM(q.user_io_wait_time) AS user_io_wait_time, SUM(q.application_wait_time) AS application_wait_time,
                       SUM(q.concurrency_wait_time) AS concurrency_wait_time, SUM(q.cluster_wait_time) AS cluster_wait_time,
                       MAX(q.last_active_time) AS last_active_time, MAX(q.parsing_schema_id) AS parsing_schema_id
                  FROM v$sql q
                 WHERE (:sql_ids IS NULL OR INSTR(',' || :sql_ids || ',', ',' || q.sql_id || ',') > 0)
                 GROUP BY q.sql_id, q.plan_hash_value
                HAVING SUM(q.executions) >= :min_executions) g
         ORDER BY g.elapsed_time DESC, g.sql_id) t
 WHERE ROWNUM <= :top
