-- 의존 관계 양방향. 바인드: owner, name, type. direction = USES(이 객체가 쓰는 것) | USED_BY(이 객체를 쓰는 것)
-- Oracle 과 달라진 점: referenced_owner/name/type 열 이름이 Tibero 에서는 parent_obj_owner/name/type 이다.
-- id: meta.detail_dependencies
-- dbms: tibero
-- min-version: 6
-- requires: dba_dependencies, all_dependencies
-- license: none
SELECT 'USES' AS direction, d.parent_obj_owner AS owner_name, d.parent_obj_name AS object_name, d.parent_obj_type AS object_type
  FROM @PFX@_dependencies d
 WHERE d.owner = :owner AND d.name = :name AND d.type = :type
UNION ALL
SELECT 'USED_BY', d.owner, d.name, d.type
  FROM @PFX@_dependencies d
 WHERE d.parent_obj_owner = :owner AND d.parent_obj_name = :name AND d.parent_obj_type = :type
 ORDER BY 1, 2, 3
