-- 테이블 통계. 바인드: owner, name (Oracle 의 meta.detail_stats 와 같은 id, 문장 동일)
-- id: meta.detail_stats
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables
-- license: none
SELECT t.num_rows, t.blocks, t.avg_row_len, t.last_analyzed, t.sample_size, t.partitioned, t.temporary, t.compression, t.tablespace_name
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
