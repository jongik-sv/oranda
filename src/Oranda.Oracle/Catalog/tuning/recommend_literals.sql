-- 튜닝 추천: 리터럴만 다른 SQL 묶음(TSK-14-17, D-051). force_matching_signature 가 같은데 sql_id 가 다른 것이 많으면 바인드 변수를 권한다.
-- gv$sqlstats 를 한 번 훑어 묶고, 묶음 수가 많은 순 상위 :top 개만 돌려준다. 표본(sql_id·본문)은 묶음에서 sql_id 가 가장 작은 것.
-- 바인드: min_count(묶음 안 sql_id 수 하한), top
-- id: tuning.recommend_literals
-- min-version: 11
-- requires: gv$sqlstats, gv$sql
-- license: none
-- rac: gv
SELECT g.fms, g.sql_count, g.executions, g.elapsed_time, g.cpu_time, g.buffer_gets, g.disk_reads, g.rows_processed, g.parse_calls,
       g.sample_inst_id, g.sample_sql_id, g.sample_text,
       (SELECT MAX(q.parsing_schema_name) FROM gv$sql q WHERE q.inst_id = g.sample_inst_id AND q.sql_id = g.sample_sql_id) AS parsing_schema_name
  FROM (SELECT TO_CHAR(s.force_matching_signature) AS fms, COUNT(DISTINCT s.sql_id) AS sql_count,
               SUM(s.executions) AS executions, SUM(s.elapsed_time) AS elapsed_time, SUM(s.cpu_time) AS cpu_time,
               SUM(s.buffer_gets) AS buffer_gets, SUM(s.disk_reads) AS disk_reads, SUM(s.rows_processed) AS rows_processed,
               SUM(s.parse_calls) AS parse_calls,
               MIN(s.inst_id) KEEP (DENSE_RANK FIRST ORDER BY s.sql_id) AS sample_inst_id,
               MIN(s.sql_id) AS sample_sql_id,
               MIN(s.sql_text) KEEP (DENSE_RANK FIRST ORDER BY s.sql_id) AS sample_text
          FROM gv$sqlstats s
         WHERE s.force_matching_signature <> 0
         GROUP BY s.force_matching_signature
        HAVING COUNT(DISTINCT s.sql_id) >= :min_count
         ORDER BY COUNT(DISTINCT s.sql_id) DESC, SUM(s.elapsed_time) DESC) g
 WHERE ROWNUM <= :top
