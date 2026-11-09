-- Top SQL 검색(Tibero V$SQL, 커서 단위). 열 순서·바인드는 Oracle sql.top 과 같다. 조건이 null 이면 그 조건은 쓰지 않는다.
-- PARSING_SCHEMA_NAME 이 없어 PARSING_SCHEMA_ID 를 ALL_USERS 로 이름으로 바꾼다(스키마 조건도 그 이름으로). inst_id = USERENV INSTANCE.
-- V$SQL 의 일부 값은 음수가 부호 없는 64비트로 넘어간 값이다(P5b H3 실측, win144 #49 — tuning/sql_stats 와 같은 규칙) →
-- 0 ~ 2^62-1 밖이면 NULL(Int64 넘침 막음). 조건·정렬도 고친 값으로 하고 NULL 은 정렬 끝에 둔다.
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
  FROM (SELECT r.sql_id, r.child_number, r.sql_text, r.parsing_schema_id, r.module, r.last_active_time,
               CASE WHEN r.executions BETWEEN 0 AND 4611686018427387903 THEN r.executions END AS executions,
               CASE WHEN r.elapsed_time BETWEEN 0 AND 4611686018427387903 THEN r.elapsed_time END AS elapsed_time,
               CASE WHEN r.cpu_time BETWEEN 0 AND 4611686018427387903 THEN r.cpu_time END AS cpu_time,
               CASE WHEN r.buffer_gets BETWEEN 0 AND 4611686018427387903 THEN r.buffer_gets END AS buffer_gets,
               CASE WHEN r.disk_reads BETWEEN 0 AND 4611686018427387903 THEN r.disk_reads END AS disk_reads,
               CASE WHEN r.rows_processed BETWEEN 0 AND 4611686018427387903 THEN r.rows_processed END AS rows_processed
          FROM v$sql r) q
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
          END DESC NULLS LAST, q.sql_id, q.child_number
 FETCH FIRST :top ROWS ONLY
