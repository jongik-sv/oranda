-- 열 통계(TN-03). 통계가 없는 열도 열 순서대로 보인다(값은 null). 바인드: owner, table_name
-- id: tuning.column_stats
-- min-version: 11
-- requires: @PFX@_tab_columns, @PFX@_tab_col_statistics
-- license: none
SELECT c.column_name, s.num_distinct, s.num_nulls, s.density, NVL(s.histogram, 'NONE') AS histogram, s.num_buckets, s.last_analyzed
  FROM @PFX@_tab_columns c
  LEFT JOIN @PFX@_tab_col_statistics s
    ON s.owner = c.owner AND s.table_name = c.table_name AND s.column_name = c.column_name
 WHERE c.owner = :owner
   AND c.table_name = :table_name
 ORDER BY c.column_id
