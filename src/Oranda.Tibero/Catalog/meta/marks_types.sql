-- 스키마 브라우저 타입 표식(VARRAY·중첩 테이블). 바인드: owner. 객체 타입은 기본이라 뺀다. 열 순서는 Oracle 판과 같다(ObjectMarkRows.CollectionType).
-- Tibero *_coll_types.coll_type 은 VARYING ARRAY(6.2.15 실측)·TABLE.
-- id: meta.marks_types
-- dbms: tibero
-- min-version: 6
-- requires: dba_coll_types, all_coll_types
-- license: none
SELECT c.type_name, c.coll_type
  FROM @PFX@_coll_types c
 WHERE c.owner = :owner
