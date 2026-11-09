-- Top SQL 검색(Tibero V$SQL, 커서 단위). 열 순서·바인드는 Oracle sql.top 과 같다. 조건이 null 이면 그 조건은 쓰지 않는다.
-- PARSING_SCHEMA_NAME 이 없어 PARSING_SCHEMA_ID 를 ALL_USERS 로 이름으로 바꾼다(스키마 조건도 그 이름으로). inst_id = USERENV INSTANCE.
-- 바인드: order_by(0 소요 시간, 1 CPU, 2 읽은 블록, 3 디스크 읽기, 4 실행 횟수, 5 실행당 소요 시간), top, min_elapsed_us, min_buffer_gets,
--         min_executions, text(SQL ID·본문 부분 문자열, 대소문자 무시), schema(파싱 스키마), active_secs(최근 N초 안에 실행됨)
-- id: sql.top
-- dbms: tibero
-- min-version: 6
-- requires: v$sql, all_users
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, q.sql_id, q.child_number, q.sql_text, q.executions,
       q.elapsed_time, q.cpu_time, q.buffer_gets, q.disk_reads, q.rows_processed, u.username AS parsing_schema_name,
       q.module, q.last_active_time
  FROM v$sql q
  LEFT JOIN all_users u ON u.user_id = q.parsing_schema_id
 WHERE (:min_elapsed_us IS NULL OR q.elapsed_time >= :min_elapsed_us)
   AND (:min_buffer_gets IS NULL OR q.buffer_gets >= :min_buffer_gets)
   AND (:min_executions IS NULL OR q.executions >= :min_executions)
   AND (:schema IS NULL OR u.username = :schema OR u.username = UPPER(:schema))
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
