-- 세션의 트랜잭션(Tibero V$TRANSACTION, 세션과는 SESS_ID 로 잇는다 — V$SESSION.TADDR 없음). 열 순서는 Oracle session.transaction 과 같다.
-- xid = USN.SLOT.WRAP(DBMS_TRANSACTION.LOCAL_TRANSACTION_ID 와 같은 모양), status = STATE 번호(글로 바꾸는 표는 쓰기 IT 로 확인),
-- used_ublk·used_urec = USED_BLK·USED_REC. 바인드: inst_id, sid, serial
-- id: session.transaction
-- dbms: tibero
-- min-version: 6
-- requires: v$transaction, v$session
-- license: none
SELECT t.usn || '.' || t.slot || '.' || t.wrap AS xid, TO_CHAR(t.state) AS status, t.start_time,
       t.used_blk AS used_ublk, t.used_rec AS used_urec, t.log_io, t.phy_io
  FROM v$transaction t
  JOIN v$session s ON s.sid = t.sess_id
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND s.sid = :sid AND s.serial# = :serial
