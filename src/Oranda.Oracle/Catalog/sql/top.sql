-- Top SQL 검색(공유 풀 gv$sql, 커서 단위). 팩 불필요 뷰만 쓴다. 조건이 null 이면 그 조건은 쓰지 않는다.
-- 바인드: order_by(0 소요 시간, 1 CPU, 2 읽은 블록, 3 디스크 읽기, 4 실행 횟수, 5 실행당 소요 시간), top, min_elapsed_us(누적 소요 마이크로초),
--         min_buffer_gets, min_executions, text(SQL ID·본문 부분 문자열, 대소문자 무시), schema(파싱 스키마), active_secs(최근 N초 안에 실행됨)
-- 열 순서는 OracleMonitorService.TopSql 의 행 매핑과 같다.
-- id: sql.top
-- min-version: 19
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT q.inst_id, q.sql_id, q.child_number, q.sql_text, q.executions, q.elapsed_time, q.cpu_time, q.buffer_gets,
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
          END DESC, q.sql_id, q.child_number
 FETCH FIRST :top ROWS ONLY
