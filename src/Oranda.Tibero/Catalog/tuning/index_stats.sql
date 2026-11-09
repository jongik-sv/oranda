-- 표의 인덱스 통계(Tibero @PFX@_INDEXES). *_IND_STATISTICS 가 없어 stale_stats = NULL. 열은 Oracle tuning.index_stats 와 같다. 바인드: owner, table_name
-- id: tuning.index_stats
-- dbms: tibero
-- min-version: 6
-- requires: dba_indexes, all_indexes
-- license: none
SELECT i.index_name, i.uniqueness, i.clustering_factor, i.leaf_blocks, i.distinct_keys, i.blevel, i.last_analyzed,
       CAST(NULL AS VARCHAR2(3)) AS stale_stats
  FROM @PFX@_indexes i
 WHERE i.table_owner = :owner
   AND i.table_name = :table_name
 ORDER BY i.index_name
