-- 열 순서 바꾸기 대상 표 종류(임시·클러스터·외부). 바인드: owner, name
-- Oracle 과 달라진 점: Tibero 의 *_tables 에는 cluster_name 열이 없어 NULL 로 채운다(클러스터 표 판정은 못 한다).
-- id: dbinfo.column_reorder_traits
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.temporary,
       CAST(NULL AS VARCHAR2(128)) AS cluster_name,
       (SELECT COUNT(*) FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name) AS external_count
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
