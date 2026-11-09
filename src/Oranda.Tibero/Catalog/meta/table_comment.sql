-- 테이블·뷰 주석. 바인드: owner, name (Oracle 판과 문장 동일)
-- id: meta.table_comment
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_comments, all_tab_comments
-- license: none
SELECT t.comments
  FROM @PFX@_tab_comments t
 WHERE t.owner = :owner
   AND t.table_name = :name
