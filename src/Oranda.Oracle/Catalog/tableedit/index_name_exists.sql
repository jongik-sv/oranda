-- 테이블 편집 검사: 스키마에 그 이름의 인덱스가 있나. 바인드: owner, name.
-- id: tableedit.index_name_exists
-- min-version: 11
-- requires: dba_indexes, all_indexes
-- license: none
SELECT COUNT(*)
  FROM @PFX@_indexes i
 WHERE i.owner = :owner
   AND i.index_name = :name
