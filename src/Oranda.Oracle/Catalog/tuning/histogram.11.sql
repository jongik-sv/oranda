-- (11g 변형: endpoint_actual_value_raw 열이 12c 부터라 읽지 않는다 — 끝점은 endpoint_actual_value 또는 endpoint_value 를 data_type 에 맞춰 푼다.)
-- 한 열의 히스토그램 버킷(TN-03). 막대 높이(앞 버킷과의 차)는 호출 쪽이 계산한다. 바인드: owner, table_name, column_name
-- id: tuning.histogram
-- min-version: 11
-- requires: @PFX@_tab_histograms, @PFX@_tab_cols
-- license: none
SELECT h.endpoint_number, h.endpoint_actual_value, TO_CHAR(h.endpoint_value) AS endpoint_value,
       (SELECT c.data_type FROM @PFX@_tab_cols c
         WHERE c.owner = h.owner AND c.table_name = h.table_name AND c.column_name = h.column_name) AS data_type
  FROM @PFX@_tab_histograms h
 WHERE h.owner = :owner
   AND h.table_name = :table_name
   AND h.column_name = :column_name
 ORDER BY h.endpoint_number
