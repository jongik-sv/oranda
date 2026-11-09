-- DDL·사전 잠금(Tibero SYS.DBA_DDL_LOCKS — PUBLIC 동의어가 순환이라 SYS. 를 붙인다). 열 순서는 Oracle lock.ddl_locks 와 같다.
-- 모드 글은 Tibero 모양 그대로(「Row-Shared(RS)」 등). 누구나 잡는 None·Row-Shared(RS) 보유는 빼고 기다리는 것과 그보다 센 보유만(최대 1000). 바인드 없음.
-- id: lock.ddl_locks
-- dbms: tibero
-- min-version: 6
-- requires: dba_ddl_locks, v$session
-- license: none
SELECT inst_id, sid, serial#, owner, name, type, mode_held, mode_requested
  FROM (SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, d.session_id AS sid, s.serial#,
               d.owner, d.name, d.type, d.mode_held, d.mode_requested
          FROM sys.dba_ddl_locks d
          JOIN v$session s ON s.sid = d.session_id
         WHERE s.type = 'WTHR'
           AND (d.mode_requested <> 'None' OR d.mode_held NOT IN ('None', 'Row-Shared(RS)'))
         ORDER BY CASE WHEN d.mode_requested <> 'None' THEN 0 ELSE 1 END, d.owner, d.name, d.session_id)
 WHERE ROWNUM <= 1000
