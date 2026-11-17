-- 테이블의 파티션 인덱스(LOCAL·GLOBAL 파티션, 테이블 편집기 T14). 나머지 인덱스 = 비파티션 전역 인덱스(나누기·합치기 뒤 통째로 다시 만들 대상). 바인드: owner, name. Oracle 판과 같다.
-- id: meta.part_indexes
-- dbms: tibero
-- min-version: 6
-- requires: dba_part_indexes, all_part_indexes
-- license: none
SELECT i.index_name, i.locality
  FROM @PFX@_part_indexes i
 WHERE i.owner = :owner
   AND i.table_name = :name
 ORDER BY i.index_name
