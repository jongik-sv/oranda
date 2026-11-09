-- 표의 기존 인덱스 열 구성(Tibero @PFX@_IND_COLUMNS, Oracle 과 같은 열). 바인드: owner, table_name
-- id: tuning.index_columns
-- dbms: tibero
-- min-version: 6
-- requires: dba_ind_columns, all_ind_columns
-- license: none
SELECT c.index_name, c.column_name, c.column_position
  FROM @PFX@_ind_columns c
 WHERE c.table_owner = :owner
   AND c.table_name = :table_name
 ORDER BY c.index_name, c.column_position
