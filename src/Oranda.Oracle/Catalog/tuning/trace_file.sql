-- 대상 세션의 트레이스 파일 서버 경로(TN-07). 바인드: inst_id, sid, serial
-- id: tuning.trace_file
-- min-version: 11
-- requires: gv$session, gv$process
-- license: none
-- rac: gv
SELECT p.tracefile
  FROM gv$session s
  JOIN gv$process p ON p.inst_id = s.inst_id AND p.addr = s.paddr
 WHERE s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial
