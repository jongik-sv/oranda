-- 한 열의 히스토그램 버킷(TN-03). 막대 높이(앞 버킷과의 차)는 호출 쪽이 계산한다. 바인드: owner, table_name, column_name
-- 끝점 글은 호출 쪽이 푼다(ColumnStatValue.HistogramEndpoint): endpoint_actual_value_raw(12c+) → endpoint_actual_value → endpoint_value 를 data_type 에 맞춰.
-- 짧은 문자 열은 endpoint_actual_value 가 비고 endpoint_value 는 앞 바이트를 담은 큰 수, DATE·TIMESTAMP 는 율리우스 일수다. 11g 는 histogram.11.sql.
-- id: tuning.histogram
-- min-version: 12
-- requires: @PFX@_tab_histograms, @PFX@_tab_cols
-- license: none
SELECT h.endpoint_number, h.endpoint_actual_value, TO_CHAR(h.endpoint_value) AS endpoint_value,
       (SELECT c.data_type FROM @PFX@_tab_cols c
         WHERE c.owner = h.owner AND c.table_name = h.table_name AND c.column_name = h.column_name) AS data_type,
       RAWTOHEX(h.endpoint_actual_value_raw) AS endpoint_actual_raw
  FROM @PFX@_tab_histograms h
 WHERE h.owner = :owner
   AND h.table_name = :table_name
   AND h.column_name = :column_name
 ORDER BY h.endpoint_number
