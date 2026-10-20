-- 표를 참조하는 PL/SQL 소스 가운데 SELECT·INSERT 가 나오는 줄. 바인드: owner, name, max_rows
-- ROWNUM 으로 줄 수를 자른다(FETCH FIRST 는 12c 이상). 참조 관계는 all_dependencies 로 좁히고(그 표를 쓰는 소스만), 줄 판정은 호출한 쪽이 한다. 줄 끝 줄바꿈은 호출한 쪽이 뗀다.
-- id: dbinfo.column_reorder_source
-- min-version: 19
-- requires: dba_source, all_source, dba_dependencies, all_dependencies
-- license: none
SELECT r.owner, r.name, r.type, r.line, r.text
  FROM (SELECT s.owner, s.name, s.type, s.line, s.text
  FROM @PFX@_source s
 WHERE s.type IN ('PACKAGE BODY', 'PROCEDURE', 'FUNCTION', 'TRIGGER', 'TYPE BODY')
   AND (INSTR(UPPER(s.text), 'SELECT') > 0 OR INSTR(UPPER(s.text), 'INSERT') > 0)
   AND EXISTS (SELECT 1
                 FROM @PFX@_dependencies d
                WHERE d.owner = s.owner AND d.name = s.name AND d.type = s.type
                  AND d.referenced_owner = :owner AND d.referenced_name = :name)
 ORDER BY s.owner, s.name, s.type, s.line) r
 WHERE ROWNUM <= :max_rows
