-- sql_id 의 자식 커서(Tibero V$SQL). 열은 Oracle tuning.child_cursors 와 같다. Tibero 에 바인드 민감·인식 열이 없어 NULL. 바인드: sql_id
-- id: tuning.child_cursors
-- dbms: tibero
-- min-version: 6
-- requires: v$sql
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, q.child_number, q.plan_hash_value,
       CAST(NULL AS VARCHAR2(1)) AS is_bind_sensitive, CAST(NULL AS VARCHAR2(1)) AS is_bind_aware, q.is_shareable,
       q.executions, q.buffer_gets, q.elapsed_time
  FROM v$sql q
 WHERE q.sql_id = :sql_id
 ORDER BY q.child_number
