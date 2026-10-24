-- 한 열의 히스토그램 버킷(TN-03). 막대 높이(앞 버킷과의 차)는 호출 쪽이 계산한다. 바인드: owner, table_name, column_name
-- id: tuning.histogram
-- min-version: 11
-- requires: @PFX@_tab_histograms
-- license: none
SELECT h.endpoint_number, h.endpoint_actual_value, TO_CHAR(h.endpoint_value) AS endpoint_value
  FROM @PFX@_tab_histograms h
 WHERE h.owner = :owner
   AND h.table_name = :table_name
   AND h.column_name = :column_name
 ORDER BY h.endpoint_number
