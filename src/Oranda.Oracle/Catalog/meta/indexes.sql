-- 인덱스와 열. 바인드: owner, name
-- id: meta.indexes
-- min-version: 11
-- requires: dba_indexes, all_indexes, dba_ind_columns, all_ind_columns
-- license: none
SELECT i.index_name, i.uniqueness, i.index_type, ic.column_name, ic.column_position
  FROM @PFX@_indexes i
  JOIN @PFX@_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
 WHERE i.table_owner = :owner
   AND i.table_name = :name
 ORDER BY i.index_name, ic.column_position
