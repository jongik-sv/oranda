-- 표 통계(TN-03). 분할 표는 표 전체 행(partition_name IS NULL)만. 바인드: owner, table_name
-- id: tuning.table_stats
-- min-version: 11
-- requires: @PFX@_tab_statistics
-- license: none
SELECT t.owner, t.table_name, t.num_rows, t.blocks, t.avg_row_len, t.last_analyzed, t.stale_stats
  FROM @PFX@_tab_statistics t
 WHERE t.owner = :owner
   AND t.table_name = :table_name
   AND t.partition_name IS NULL
   AND t.object_type = 'TABLE'
