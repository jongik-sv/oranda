-- 세션 I/O 누계(gv$sess_io, 팩 불필요). 바인드: inst_id, sid
-- id: session.io
-- min-version: 11
-- requires: gv$sess_io
-- license: none
-- rac: gv
SELECT i.block_gets, i.consistent_gets, i.physical_reads, i.block_changes, i.consistent_changes
  FROM gv$sess_io i
 WHERE i.inst_id = :inst_id AND i.sid = :sid
