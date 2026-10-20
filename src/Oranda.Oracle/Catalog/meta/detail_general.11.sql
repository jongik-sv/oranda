-- 객체 일반 정보(11g 변형: editionable 열이 없어 NULL). 바인드: owner, name, type
-- id: meta.detail_general
-- min-version: 11
-- requires: dba_objects, all_objects
-- license: none
SELECT o.object_type, o.status, o.created, o.last_ddl_time, CAST(NULL AS VARCHAR2(1)) AS edition_flag, o.object_id
  FROM @PFX@_objects o
 WHERE o.owner = :owner
   AND o.object_name = :name
   AND o.object_type = :type
   AND o.subobject_name IS NULL
