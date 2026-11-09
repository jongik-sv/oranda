-- 객체 일반 정보. 바인드: owner, name, type
-- Tibero 의 *_objects 에는 editionable 열이 없어 edition_flag 는 NULL(Oracle 11g 변형과 같다). object_id 는 그대로 있다.
-- id: meta.detail_general
-- dbms: tibero
-- min-version: 6
-- requires: dba_objects, all_objects
-- license: none
SELECT o.object_type, o.status, o.created, o.last_ddl_time, CAST(NULL AS VARCHAR2(1)) AS edition_flag, o.object_id
  FROM @PFX@_objects o
 WHERE o.owner = :owner
   AND o.object_name = :name
   AND o.object_type = :type
   AND o.subobject_name IS NULL
