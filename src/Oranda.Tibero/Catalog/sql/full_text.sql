-- SQL 전문(Tibero). V$SQL.SQL_TEXT 가 전문이다(SQL_FULLTEXT 열 없음, session.sql_text 와 같은 근거). 열 이름은 Oracle 과 같게 sql_fulltext.
-- 바인드: inst_id(null=아무 인스턴스), sql_id
-- id: sql.full_text
-- dbms: tibero
-- min-version: 6
-- requires: v$sql
-- license: none
SELECT q.sql_text AS sql_fulltext
  FROM v$sql q
 WHERE q.sql_id = :sql_id AND (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
 FETCH FIRST 1 ROWS ONLY
