-- PL/SQL 소스 줄(all_source/dba_source). 명세와 본문은 type(PACKAGE / PACKAGE BODY)으로 구분. 바인드: owner, name, type
-- id: meta.source
-- min-version: 19
-- requires: dba_source, all_source
-- license: none
SELECT s.text
  FROM @PFX@_source s
 WHERE s.owner = :owner
   AND s.name = :name
   AND s.type = :type
 ORDER BY s.line
