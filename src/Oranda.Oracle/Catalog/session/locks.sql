-- 세션이 잠근 객체. @PFX@ = dba 또는 all. 바인드: inst_id, sid
-- id: session.locks
-- min-version: 19
-- requires: gv$locked_object, dba_objects, all_objects
-- license: none
-- rac: gv
SELECT o.owner, o.object_name, o.object_type,
       DECODE(lo.locked_mode, 0, 'None', 1, 'Null', 2, 'Row-S (SS)', 3, 'Row-X (SX)', 4, 'Share',
              5, 'S/Row-X (SSX)', 6, 'Exclusive', TO_CHAR(lo.locked_mode)) AS locked_mode
  FROM gv$locked_object lo
  LEFT JOIN @PFX@_objects o ON o.object_id = lo.object_id
 WHERE lo.inst_id = :inst_id AND lo.session_id = :sid
 ORDER BY o.owner, o.object_name
