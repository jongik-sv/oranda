-- AI 파티션 추천: 표의 열 이름(조건 글에서 열을 찾을 때 비교할 목록). 바인드: owner, name
-- id: advisor.table_columns
-- min-version: 11
-- requires: dba_tab_columns, all_tab_columns
-- license: none
SELECT c.column_name
  FROM @PFX@_tab_columns c
 WHERE c.owner = :owner
   AND c.table_name = :name
 ORDER BY c.column_id
