-- 파티션 목록. 바인드: owner, name. high_value 는 LONG(InitialLONGFetchSize = -1 로 읽는다).
-- id: meta.detail_partitions
-- min-version: 19
-- requires: dba_tab_partitions, all_tab_partitions, dba_part_tables, all_part_tables
-- license: none
SELECT p.partition_position, p.partition_name, pt.partitioning_type, pt.subpartitioning_type,
       p.high_value, p.tablespace_name, p.num_rows, p.last_analyzed
  FROM @PFX@_tab_partitions p
  JOIN @PFX@_part_tables pt ON pt.owner = p.table_owner AND pt.table_name = p.table_name
 WHERE p.table_owner = :owner
   AND p.table_name = :name
 ORDER BY p.partition_position
