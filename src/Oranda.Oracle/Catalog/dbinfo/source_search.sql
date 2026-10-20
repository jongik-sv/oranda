-- PL/SQL 소스 본문 검색(줄 단위). 바인드: owner, types('|PACKAGE|PACKAGE BODY|' 형식), needle, pattern, flags('c' 또는 'i'), max_rows
-- 검색어는 바인드로만 받는다. needle 은 대소문자 무시 INSTR 로 먼저 거르는 글자(정규식 모드면 NULL), pattern 은 REGEXP_LIKE 패턴.
-- 줄 끝 줄바꿈은 떼고 맞춘다(패턴의 $ 가 줄 끝에 맞도록). 소스 편집기로 열 수 있는 7개 유형만 찾는다.
-- id: dbinfo.source_search
-- min-version: 19
-- requires: dba_source, all_source
-- license: none
SELECT s.owner, s.name, s.type, s.line, s.text
  FROM @PFX@_source s
 WHERE (:owner IS NULL OR s.owner = :owner)
   AND s.type IN ('PACKAGE', 'PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE', 'TYPE BODY')
   AND (:types IS NULL OR INSTR(:types, '|' || s.type || '|') > 0)
   AND (:needle IS NULL OR INSTR(UPPER(s.text), UPPER(:needle)) > 0)
   AND REGEXP_LIKE(RTRIM(s.text, CHR(13) || CHR(10)), :pattern, :flags)
 ORDER BY s.owner, s.name, s.type, s.line
 FETCH FIRST :max_rows ROWS ONLY
