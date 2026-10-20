-- 표 정의가 마지막으로 바뀐 시각(플래시백이 그 이전으로는 거슬러 갈 수 없다, ORA-01466).
-- id: dbinfo.flashback_ddl
-- min-version: 11
-- requires: all_objects
-- license: none
SELECT o.last_ddl_time
  FROM all_objects o
 WHERE o.owner = :owner
   AND o.object_name = :name
   AND o.object_type = 'TABLE'
