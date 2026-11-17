-- 테이블 편집 검사: 열 하나의 지금 정의(숫자 정밀도·소수 줄이기 판정, 실측 ORA-01440). 바인드: owner, name, column_name.
-- id: tableedit.column_def
-- min-version: 11
-- requires: dba_tab_cols, all_tab_cols
-- license: none
SELECT c.data_type, c.data_precision, c.data_scale
  FROM @PFX@_tab_cols c
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.column_name = :column_name
