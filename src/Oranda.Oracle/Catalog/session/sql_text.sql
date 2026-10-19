-- SQL 전문(현재·직전 SQL). 바인드: inst_id, sql_id
-- id: session.sql_text
-- min-version: 19
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT q.sql_fulltext
  FROM gv$sql q
 WHERE q.inst_id = :inst_id AND q.sql_id = :sql_id
 FETCH FIRST 1 ROWS ONLY
