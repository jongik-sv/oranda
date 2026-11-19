-- 스키마 브라우저 타입 표식(VARRAY·중첩 테이블). 바인드: owner. 객체 타입은 기본이라 뺀다(컬렉션 타입만 all_coll_types 에 있다).
-- 열(ObjectMarkRows.CollectionType): type_name, coll_type(VARYING ARRAY·TABLE).
-- id: meta.marks_types
-- min-version: 11
-- requires: dba_coll_types, all_coll_types
-- license: none
SELECT c.type_name, c.coll_type
  FROM @PFX@_coll_types c
 WHERE c.owner = :owner
