-- 대상 세션의 트레이스 파일 경로와 진단 트레이스 폴더(v$diag_info 'Diag Trace'). 바인드: inst_id, sid, serial
-- id: session.trace_file
-- min-version: 11
-- requires: gv$session, gv$process, gv$diag_info
-- license: none
-- rac: gv
SELECT p.tracefile,
       (SELECT d.value FROM gv$diag_info d WHERE d.inst_id = s.inst_id AND d.name = 'Diag Trace') AS diag_trace
  FROM gv$session s
  JOIN gv$process p ON p.inst_id = s.inst_id AND p.addr = s.paddr
 WHERE s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial
