-- 현재 스키마의 휴지통(user_recyclebin).
-- id: dbinfo.recyclebin
-- min-version: 11
-- requires: user_recyclebin
-- license: none
SELECT r.object_name, r.original_name, r.type, r.ts_name, r.droptime, r.can_undrop, r.can_purge, r.space
  FROM user_recyclebin r
 ORDER BY r.droptime DESC, r.object_name
