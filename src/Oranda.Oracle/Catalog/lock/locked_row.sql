-- 대기 세션이 기다리는 잠긴 행(LK-06). 행 잠금 대기(enq: TX - row lock contention)이고 row_wait_obj# 가 있을 때만 한 행.
-- rowid = DBMS_ROWID.ROWID_CREATE(1, data_object_id, 상대 파일 번호, block#, row#). row_wait_file# 은 절대 번호라 v$datafile.rfile# 로 바꾼다.
-- 열 4~6 = 상대 파일 번호·블록·행(화면 「파일 7 · 블록 2495 · 행 2」). 바인드: inst_id, sid, serial. @PFX@ = dba 또는 all(보이지 않는 객체면 행 없음).
-- id: lock.locked_row
-- min-version: 11
-- requires: gv$session, v$datafile, dba_objects, all_objects
-- license: none
-- rac: gv
SELECT o.owner, o.object_name, o.object_type,
       DBMS_ROWID.ROWID_CREATE(1, o.data_object_id, NVL(f.rfile#, s.row_wait_file#), s.row_wait_block#, s.row_wait_row#) AS locked_rowid,
       NVL(f.rfile#, s.row_wait_file#) AS rel_file, s.row_wait_block#, s.row_wait_row#
  FROM gv$session s
  JOIN @PFX@_objects o ON o.object_id = s.row_wait_obj#
  LEFT JOIN v$datafile f ON f.file# = s.row_wait_file#
 WHERE s.inst_id = :inst_id AND s.sid = :sid AND s.serial# = :serial
   AND s.event = 'enq: TX - row lock contention'
   AND s.row_wait_obj# > 0
   AND o.data_object_id IS NOT NULL
