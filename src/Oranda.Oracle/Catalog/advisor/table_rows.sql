-- AI 파티션 추천: 통계 행 수(NUM_ROWS, 통계가 없으면 NULL). 표본 비율(PA-1)을 정하는 데 쓴다. 바인드: owner, name
-- id: advisor.table_rows
-- min-version: 11
-- requires: dba_tables, all_tables
-- license: none
SELECT t.num_rows
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
