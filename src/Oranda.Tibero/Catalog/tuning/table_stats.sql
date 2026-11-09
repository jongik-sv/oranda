-- 표 통계(Tibero). *_TAB_STATISTICS 가 없어 @PFX@_TABLES 의 통계 열을 쓴다. 열은 Oracle tuning.table_stats 와 같다.
-- 오래된 통계 표시(STALE_STATS)가 Tibero 에 없어 NULL. 바인드: owner, table_name
-- id: tuning.table_stats
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables
-- license: none
SELECT t.owner, t.table_name, t.num_rows, t.blocks, t.avg_row_len, t.last_analyzed, CAST(NULL AS VARCHAR2(3)) AS stale_stats
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :table_name
