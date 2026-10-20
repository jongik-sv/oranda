-- 열 순서 바꾸기 대상 표 종류(임시·클러스터·외부). 바인드: owner, name
-- id: dbinfo.column_reorder_traits
-- min-version: 11
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.temporary,
       t.cluster_name,
       (SELECT COUNT(*) FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name) AS external_count
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
