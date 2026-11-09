-- 표를 참조하는 PL/SQL 소스 가운데 SELECT·INSERT 가 나오는 줄. 바인드: owner, name, max_rows
-- Oracle 과 달라진 점: *_dependencies 의 참조 대상 열 이름이 parent_obj_owner / parent_obj_name 이다. ROWNUM 으로 줄 수를 자른다. 줄 판정·줄 끝 줄바꿈은 호출한 쪽이 처리한다.
-- id: dbinfo.column_reorder_source
-- dbms: tibero
-- min-version: 6
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
                  AND d.parent_obj_owner = :owner AND d.parent_obj_name = :name)
 ORDER BY s.owner, s.name, s.type, s.line) r
 WHERE ROWNUM <= :max_rows
