-- sql_id 의 자식 커서(Tibero V$SQL). 열은 Oracle tuning.child_cursors 와 같다. Tibero 에 바인드 민감·인식 열이 없어 NULL. 바인드: sql_id
-- V$SQL 의 일부 값은 음수가 부호 없는 64비트로 넘어간 값이다(P5b H3 실측, win144 #49) → 0 ~ 2^62-1 밖이면 NULL(Int64 넘침 막음).
-- id: tuning.child_cursors
-- dbms: tibero
-- min-version: 6
-- requires: v$sql
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, q.child_number, q.plan_hash_value,
       CAST(NULL AS VARCHAR2(1)) AS is_bind_sensitive, CAST(NULL AS VARCHAR2(1)) AS is_bind_aware, q.is_shareable,
       CASE WHEN q.executions BETWEEN 0 AND 4611686018427387903 THEN q.executions END AS executions, CASE WHEN q.buffer_gets BETWEEN 0 AND 4611686018427387903 THEN q.buffer_gets END AS buffer_gets, CASE WHEN q.elapsed_time BETWEEN 0 AND 4611686018427387903 THEN q.elapsed_time END AS elapsed_time
  FROM v$sql q
 WHERE q.sql_id = :sql_id
 ORDER BY q.child_number
