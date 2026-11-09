-- PL/SQL 소스 본문 검색. 바인드: owner, types, needle, pattern, flags, max_rows (Oracle 11g 변형과 같은 문장, ROWNUM 감싸기).
-- Tibero 의 REGEXP_LIKE 는 WHERE 절에서 쓴다(Oracle 판과 같은 형태).
-- id: dbinfo.source_search
-- dbms: tibero
-- min-version: 6
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
