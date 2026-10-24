-- DDL·라이브러리 잠금(LK-09). dba_ddl_locks(catblock.sql 로 만드는 뷰, 이 인스턴스만). 누구나 잡는 Null 보유는 빼고
-- 기다리는 것(mode_requested 가 None 아님)과 Share·Exclusive 보유만(최대 1000). 바인드 없음.
-- id: lock.ddl_locks
-- min-version: 11
-- requires: dba_ddl_locks, v$session
-- license: none
-- rac: v
SELECT inst_id, sid, serial#, owner, name, type, mode_held, mode_requested
  FROM (SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, d.session_id AS sid, s.serial#,
               d.owner, d.name, d.type, d.mode_held, d.mode_requested
          FROM dba_ddl_locks d
          JOIN v$session s ON s.sid = d.session_id
         WHERE s.type <> 'BACKGROUND'
           AND (d.mode_requested <> 'None' OR d.mode_held IN ('Share', 'Exclusive'))
         ORDER BY CASE WHEN d.mode_requested <> 'None' THEN 0 ELSE 1 END, d.owner, d.name, d.session_id)
 WHERE ROWNUM <= 1000
