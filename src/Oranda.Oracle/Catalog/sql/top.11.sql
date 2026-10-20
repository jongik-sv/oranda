-- Top SQL 검색(11g 변형: FETCH FIRST 대신 ROWNUM 감싸기). 바인드·열 순서는 top.sql 과 같다.
-- id: sql.top
-- min-version: 11
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT inst_id, sql_id, child_number, sql_text, executions, elapsed_time, cpu_time, buffer_gets,
       disk_reads, rows_processed, parsing_schema_name, module, last_active_time
  FROM (SELECT q.inst_id, q.sql_id, q.child_number, q.sql_text, q.executions, q.elapsed_time, q.cpu_time, q.buffer_gets,
               q.disk_reads, q.rows_processed, q.parsing_schema_name, q.module, q.last_active_time
          FROM gv$sql q
         WHERE (:min_elapsed_us IS NULL OR q.elapsed_time >= :min_elapsed_us)
           AND (:min_buffer_gets IS NULL OR q.buffer_gets >= :min_buffer_gets)
           AND (:min_executions IS NULL OR q.executions >= :min_executions)
           AND (:schema IS NULL OR q.parsing_schema_name = :schema OR q.parsing_schema_name = UPPER(:schema))
           AND (:active_secs IS NULL OR q.last_active_time >= SYSDATE - :active_secs / 86400)
           AND (:text IS NULL OR q.sql_id = :text OR INSTR(UPPER(q.sql_text), UPPER(:text)) > 0)
         ORDER BY CASE :order_by
                    WHEN 0 THEN q.elapsed_time
                    WHEN 1 THEN q.cpu_time
                    WHEN 2 THEN q.buffer_gets
                    WHEN 3 THEN q.disk_reads
                    WHEN 4 THEN q.executions
                    ELSE q.elapsed_time / GREATEST(q.executions, 1)
                  END DESC, q.sql_id, q.child_number)
 WHERE ROWNUM <= :top
