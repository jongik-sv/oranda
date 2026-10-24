-- 표의 기존 인덱스 열 구성(TN-09 후보와 겹침 확인). 바인드: owner, table_name
-- id: tuning.index_columns
-- min-version: 11
-- requires: @PFX@_ind_columns
-- license: none
SELECT c.index_name, c.column_name, c.column_position
  FROM @PFX@_ind_columns c
 WHERE c.table_owner = :owner
   AND c.table_name = :table_name
 ORDER BY c.index_name, c.column_position
