-- sql_id 의 자식 커서(TN-04): 바인드 민감·인식·공유 가능, 계획 해시, 누적 실행 통계. 바인드: sql_id
-- id: tuning.child_cursors
-- min-version: 11
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT q.inst_id, q.child_number, q.plan_hash_value, q.is_bind_sensitive, q.is_bind_aware, q.is_shareable,
       q.executions, q.buffer_gets, q.elapsed_time
  FROM gv$sql q
 WHERE q.sql_id = :sql_id
 ORDER BY q.inst_id, q.child_number
