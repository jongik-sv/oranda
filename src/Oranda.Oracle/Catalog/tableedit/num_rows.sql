-- 테이블 편집 검사 제목줄: 통계 행 수(NUM_ROWS, 통계가 없으면 NULL). 바인드: owner, name.
-- id: tableedit.num_rows
-- min-version: 11
-- requires: dba_tables, all_tables
-- license: none
SELECT t.num_rows
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
