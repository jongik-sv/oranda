-- 테이블 통계. 바인드: owner, name
-- id: meta.detail_stats
-- min-version: 11
-- requires: dba_tables, all_tables
-- license: none
SELECT t.num_rows, t.blocks, t.avg_row_len, t.last_analyzed, t.sample_size, t.partitioned, t.temporary, t.compression, t.tablespace_name
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
