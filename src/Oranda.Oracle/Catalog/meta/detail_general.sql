-- 객체 일반 정보(생성·변경 시각, 상태). 바인드: owner, name, type
-- id: meta.detail_general
-- min-version: 19
-- requires: dba_objects, all_objects
-- license: none
SELECT o.object_type, o.status, o.created, o.last_ddl_time, o.editionable, o.object_id
  FROM @PFX@_objects o
 WHERE o.owner = :owner
   AND o.object_name = :name
   AND o.object_type = :type
   AND o.subobject_name IS NULL
