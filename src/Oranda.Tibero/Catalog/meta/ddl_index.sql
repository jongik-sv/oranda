-- 인덱스 하나(소유자·이름으로)의 머리와 열. 바인드: owner, name(인덱스 소유자·이름). 한 줄 = 인덱스 열 하나, meta.ddl_table_indexes 와 같은 열 모양.
-- 열 owner, index_name, table_owner, table_name, uniqueness, index_type, partitioned, column_position, column_name, descend, column_expression.
-- 쓰는 *_INDEXES·*_IND_COLUMNS·*_IND_EXPRESSIONS 열은 meta.indexes·meta.index_columns 가 이미 쓰는 열이다(실측표 «있음»).
-- 파티션 인덱스 여부는 *_INDEXES.PARTITIONED 가 실측표에 없어서, 실측된 *_OBJECTS 열(OWNER·OBJECT_NAME·OBJECT_TYPE)로 인덱스 파티션 행이 있는지를 본다.
-- Tibero 의 인덱스 파티션 OBJECT_TYPE 이름(Oracle 은 INDEX PARTITION·INDEX SUBPARTITION)과 INDEX_TYPE 값은 재지 않았다.
-- id: meta.ddl_index
-- dbms: tibero
-- min-version: 6
-- requires: dba_indexes, all_indexes, dba_ind_columns, all_ind_columns, dba_ind_expressions, all_ind_expressions, dba_objects, all_objects
-- license: none
SELECT i.owner, i.index_name, i.table_owner, i.table_name, i.uniqueness, i.index_type,
       CASE WHEN EXISTS (SELECT 1
                           FROM @PFX@_objects o
                          WHERE o.owner = i.owner
                            AND o.object_name = i.index_name
                            AND o.object_type IN ('INDEX PARTITION', 'INDEX SUBPARTITION'))
            THEN 'YES' ELSE 'NO' END AS partitioned,
       ic.column_position, ic.column_name, ic.descend, ie.column_expression
  FROM @PFX@_indexes i
  JOIN @PFX@_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
  LEFT JOIN @PFX@_ind_expressions ie
    ON ie.index_owner = ic.index_owner
   AND ie.index_name = ic.index_name
   AND ie.column_position = ic.column_position
 WHERE i.owner = :owner
   AND i.index_name = :name
 ORDER BY ic.column_position
