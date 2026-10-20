-- PL/SQL 소스 본문 검색(11g 변형: FETCH FIRST 대신 ROWNUM 감싸기). 바인드는 source_search.sql 과 같다.
-- id: dbinfo.source_search
-- min-version: 11
-- requires: dba_source, all_source
-- license: none
SELECT owner, name, type, line, text
  FROM (SELECT s.owner, s.name, s.type, s.line, s.text
          FROM @PFX@_source s
         WHERE (:owner IS NULL OR s.owner = :owner)
           AND s.type IN ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'TYPE BODY')
           AND (:types IS NULL OR INSTR(:types, '|' || s.type || '|') > 0)
           AND (:needle IS NULL OR INSTR(UPPER(s.text), UPPER(:needle)) > 0)
           AND REGEXP_LIKE(RTRIM(s.text, CHR(13) || CHR(10)), :pattern, :flags)
         ORDER BY s.owner, s.name, s.type, s.line)
 WHERE ROWNUM <= :max_rows
