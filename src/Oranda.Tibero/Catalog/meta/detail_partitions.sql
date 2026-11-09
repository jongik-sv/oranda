-- 파티션 목록. 바인드: owner, name.
-- Oracle 과 달라진 점: *_tab_partitions 의 열 이름이 table_owner → owner, partition_position → partition_no, high_value → bound 이다.
-- 결과 열 이름은 Oracle 판과 같게 별칭을 붙였다.
-- id: meta.detail_partitions
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_partitions, all_tab_partitions, dba_part_tables, all_part_tables
-- license: none
SELECT p.partition_no AS partition_position, p.partition_name, pt.partitioning_type, pt.subpartitioning_type,
       p.bound AS high_value, p.tablespace_name, p.num_rows, p.last_analyzed
  FROM @PFX@_tab_partitions p
  JOIN @PFX@_part_tables pt ON pt.owner = p.owner AND pt.table_name = p.table_name
 WHERE p.owner = :owner
   AND p.table_name = :name
 ORDER BY p.partition_no
