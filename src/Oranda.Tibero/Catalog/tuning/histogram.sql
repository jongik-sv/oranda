-- 한 열의 히스토그램 버킷(Tibero @PFX@_TAB_HISTOGRAMS, Oracle 과 같은 열, _RAW 없음). 막대 높이(앞 버킷과의 차)는 호출 쪽이 계산한다. 바인드: owner, table_name, column_name
-- ENDPOINT_ACTUAL_VALUE 가 늘 채워지는 글(low_value 와 같은 형식)이고, 문자 열의 ENDPOINT_VALUE 는 쓸 수 없는 값(65.25 등)이다 — 끝점 글은 TiberoStatValue 가 data_type 에 맞춰 푼다.
-- id: tuning.histogram
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_histograms, all_tab_histograms, dba_tab_columns, all_tab_columns
-- license: none
SELECT h.endpoint_number, h.endpoint_actual_value, TO_CHAR(h.endpoint_value) AS endpoint_value,
       (SELECT c.data_type FROM @PFX@_tab_columns c
         WHERE c.owner = h.owner AND c.table_name = h.table_name AND c.column_name = h.column_name) AS data_type
  FROM @PFX@_tab_histograms h
 WHERE h.owner = :owner
   AND h.table_name = :table_name
   AND h.column_name = :column_name
 ORDER BY h.endpoint_number
