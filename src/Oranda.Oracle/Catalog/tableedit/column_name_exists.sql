-- 테이블 편집 검사: 테이블에 그 이름의 열(숨은 열 포함)이 있나. 바인드: owner, name, column_name.
-- id: tableedit.column_name_exists
-- min-version: 11
-- requires: dba_tab_cols, all_tab_cols
-- license: none
SELECT COUNT(*)
  FROM @PFX@_tab_cols c
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.column_name = :column_name
