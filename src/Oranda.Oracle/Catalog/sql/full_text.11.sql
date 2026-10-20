-- SQL 전문(11g 변형: FETCH FIRST 대신 ROWNUM). 바인드: inst_id(null=아무 인스턴스), sql_id
-- id: sql.full_text
-- min-version: 11
-- requires: gv$sql
-- license: none
-- rac: gv
SELECT q.sql_fulltext
  FROM gv$sql q
 WHERE q.sql_id = :sql_id AND (:inst_id IS NULL OR q.inst_id = :inst_id)
   AND ROWNUM <= 1
