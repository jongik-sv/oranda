-- 세션 I/O 누계(Tibero V$SESSION_IO, Oracle V$SESS_IO 와 같은 열). 바인드: inst_id, sid
-- id: session.io
-- dbms: tibero
-- min-version: 6
-- requires: v$session_io
-- license: none
SELECT i.block_gets, i.consistent_gets, i.physical_reads, i.block_changes, i.consistent_changes
  FROM v$session_io i
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND i.sid = :sid
