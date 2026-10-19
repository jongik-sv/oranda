-- 테이블·뷰 주석. 바인드: owner, name
-- id: meta.table_comment
-- min-version: 19
-- requires: dba_tab_comments, all_tab_comments
-- license: none
SELECT t.comments
  FROM @PFX@_tab_comments t
 WHERE t.owner = :owner
   AND t.table_name = :name
