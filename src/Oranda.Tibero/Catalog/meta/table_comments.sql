-- 소유자 하나의 테이블·뷰 주석을 한 번에(스키마 브라우저 트리가 이름 옆에 보인다). 주석 있는 것만. 바인드: owner (Oracle 판과 문장 동일)
-- id: meta.table_comments
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_comments, all_tab_comments
-- license: none
SELECT t.table_name, t.comments
  FROM @PFX@_tab_comments t
 WHERE t.owner = :owner
   AND t.comments IS NOT NULL
