-- 컴파일 오류·경고(all_errors/dba_errors). 줄·열은 1부터. 바인드: owner, name, type
-- id: meta.compile_errors
-- min-version: 19
-- requires: dba_errors, all_errors
-- license: none
SELECT e.line, e.position, e.text, e.attribute, e.message_number
  FROM @PFX@_errors e
 WHERE e.owner = :owner
   AND e.name = :name
   AND e.type = :type
 ORDER BY e.sequence
