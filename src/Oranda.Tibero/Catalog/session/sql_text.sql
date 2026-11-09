-- SQL 전문(Tibero). V$SQL 에 SQL_FULLTEXT 가 없으나 SQL_TEXT 가 전문이다(실측: 10,957자 문장의 SQL_TEXT 길이 = V$SQLTEXT 조각 합).
-- 열 이름은 Oracle 과 같게 sql_fulltext. 바인드: inst_id, sql_id
-- id: session.sql_text
-- dbms: tibero
-- min-version: 6
-- requires: v$sql
-- license: none
SELECT q.sql_text AS sql_fulltext
  FROM v$sql q
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND q.sql_id = :sql_id
 FETCH FIRST 1 ROWS ONLY
