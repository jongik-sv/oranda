-- 표의 인덱스 통계(TN-03). 값은 @PFX@_indexes 에서, stale 은 @PFX@_ind_statistics 에서(분할 인덱스는 인덱스 전체 행만).
-- all_ind_statistics 는 남의 스키마 인덱스를 보이지 않아(표 SELECT 권한만으로는 0행) 비 DBA 는 stale 이 늘 'NO' 로 보인다. 바인드: owner, table_name
-- id: tuning.index_stats
-- min-version: 11
-- requires: @PFX@_indexes, @PFX@_ind_statistics
-- license: none
SELECT i.index_name, i.uniqueness, i.clustering_factor, i.leaf_blocks, i.distinct_keys, i.blevel, i.last_analyzed, s.stale_stats
  FROM @PFX@_indexes i
  LEFT JOIN @PFX@_ind_statistics s
    ON s.owner = i.owner AND s.index_name = i.index_name AND s.partition_name IS NULL AND s.object_type = 'INDEX'
 WHERE i.table_owner = :owner
   AND i.table_name = :table_name
 ORDER BY i.index_name
