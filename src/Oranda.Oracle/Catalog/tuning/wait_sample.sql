-- 대기 표본 한 번(TN-12, 팩 없음 — ASH 대신 gv$session 을 직접 본다). 대기 중이 아니면(CPU) event·wait_class 는 null.
-- 바인드: sql_id(null = 내 세션)
-- id: tuning.wait_sample
-- min-version: 11
-- requires: gv$session
-- license: none
-- rac: gv
SELECT s.inst_id, s.sid, s.sql_id,
       CASE WHEN s.state = 'WAITING' THEN s.event END AS event,
       CASE WHEN s.state = 'WAITING' THEN s.wait_class END AS wait_class
  FROM gv$session s
 WHERE (:sql_id IS NULL AND s.inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AND s.sid = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')))
    OR (s.sql_id = :sql_id AND s.status = 'ACTIVE')
 ORDER BY s.inst_id, s.sid
