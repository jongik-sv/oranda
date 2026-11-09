-- 표 하나의 인덱스 전체(인덱스 소유자 포함)의 머리와 열 — 종속 DDL 의 CREATE INDEX 를 만든다. 바인드: owner, name(표). 한 줄 = 인덱스 열 하나.
-- 열 모양은 meta.ddl_index 와 같다(owner, index_name, table_owner, table_name, uniqueness, index_type, partitioned, column_position, column_name, descend, column_expression).
-- 읽는 쪽은 (owner, index_name) 으로 묶는다(다른 스키마의 같은 이름 인덱스가 섞이지 않게). 재지 않은 값은 meta.ddl_index 머리말과 같다.
-- id: meta.ddl_table_indexes
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
 WHERE i.table_owner = :owner
   AND i.table_name = :name
 ORDER BY i.owner, i.index_name, ic.column_position
