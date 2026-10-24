-- 내 세션이 방금 끝낸 문장의 sql_id·자식·계획 해시(벤치마크 TN-10). 이 질의 자체가 실행 중이라 prev_* 가 직전 문장이다.
-- id: tuning.prev_cursor
-- min-version: 11
-- requires: v$session, v$sql
-- license: none
SELECT s.prev_sql_id, s.prev_child_number,
       (SELECT q.plan_hash_value FROM v$sql q
         WHERE q.sql_id = s.prev_sql_id AND q.child_number = s.prev_child_number AND ROWNUM = 1) AS plan_hash_value
  FROM v$session s
 WHERE s.sid = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))
