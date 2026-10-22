-- 인덱스 하나의 열(위치·열 이름·내림차순·함수 식). 객체 상세에서 인덱스 줄을 고를 때 읽는다(TSK-13-34). 바인드: owner, name, index_name
-- id: meta.index_columns
-- min-version: 11
-- requires: dba_indexes, all_indexes, dba_ind_columns, all_ind_columns, dba_ind_expressions, all_ind_expressions
-- license: none
SELECT ic.column_position, ic.column_name, ic.descend, ie.column_expression
  FROM @PFX@_indexes i
  JOIN @PFX@_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
  LEFT JOIN @PFX@_ind_expressions ie
    ON ie.index_owner = ic.index_owner
   AND ie.index_name = ic.index_name
   AND ie.column_position = ic.column_position
 WHERE i.table_owner = :owner
   AND i.table_name = :name
   AND i.index_name = :index_name
 ORDER BY ic.column_position
