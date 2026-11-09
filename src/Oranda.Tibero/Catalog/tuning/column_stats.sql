-- 열 통계(Tibero). 통계가 없는 열도 열 순서대로 보인다(값은 null). 열은 Oracle tuning.column_stats 와 같다.
-- Tibero *_TAB_COL_STATISTICS 는 소유자 열이 TABLE_OWNER 이고 HISTOGRAM 열이 없다 → 버킷이 2개 이상이면 'HISTOGRAM', 아니면 'NONE'
-- (Oracle 의 종류 이름 FREQUENCY·HEIGHT BALANCED 등은 Tibero 에서 알 수 없다). 바인드: owner, table_name
-- id: tuning.column_stats
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_columns, all_tab_columns, dba_tab_col_statistics, all_tab_col_statistics
-- license: none
SELECT c.column_name, s.num_distinct, s.num_nulls, s.density,
       CASE WHEN NVL(s.num_buckets, 0) > 1 THEN 'HISTOGRAM' ELSE 'NONE' END AS histogram, s.num_buckets, s.last_analyzed
  FROM @PFX@_tab_columns c
  LEFT JOIN @PFX@_tab_col_statistics s
    ON s.table_owner = c.owner AND s.table_name = c.table_name AND s.column_name = c.column_name
 WHERE c.owner = :owner
   AND c.table_name = :table_name
 ORDER BY c.column_id
