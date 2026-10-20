-- 참조 관계 양방향. 바인드: owner, name, type. direction = USES(이 객체가 쓰는 것) | USED_BY(이 객체를 쓰는 것)
-- id: dbinfo.references
-- min-version: 11
-- requires: dba_dependencies, all_dependencies
-- license: none
SELECT 'USES' AS direction, d.referenced_owner AS owner_name, d.referenced_name AS object_name, d.referenced_type AS object_type
  FROM @PFX@_dependencies d
 WHERE d.owner = :owner AND d.name = :name AND d.type = :type
UNION ALL
SELECT 'USED_BY', d.owner, d.name, d.type
  FROM @PFX@_dependencies d
 WHERE d.referenced_owner = :owner AND d.referenced_name = :name AND d.referenced_type = :type
 ORDER BY 1, 2, 3
