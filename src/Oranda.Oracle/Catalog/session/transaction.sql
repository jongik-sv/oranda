-- 세션의 트랜잭션(undo 사용량). 바인드: inst_id, sid, serial
-- id: session.transaction
-- min-version: 11
-- requires: gv$transaction, gv$session
-- license: none
-- rac: gv
SELECT t.xidusn || '.' || t.xidslot || '.' || t.xidsqn AS xid, t.status, t.start_time,
       t.used_ublk, t.used_urec, t.log_io, t.phy_io
  FROM gv$transaction t
  JOIN gv$session s ON s.inst_id = t.inst_id AND s.taddr = t.addr
 WHERE s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial
