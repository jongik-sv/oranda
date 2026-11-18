-- 객체에 기대는 것들(TSK-15-09, AI 도구 get_dependents): 무효화될 객체 · 이 표를 참조하는 FK · 이 표의 트리거 · 이 객체를 가리키는 동의어. 바인드: owner, name
-- 열 = kind(DEPENDENT | FK | TRIGGER | SYNONYM), owner, name, detail(형식 · 제약 이름 · 사건), status. 자기 자신(패키지 본문 등)은 뺀다.
-- id: tuning.object_dependents
-- min-version: 11
-- requires: @PFX@_dependencies, @PFX@_objects, @PFX@_constraints, @PFX@_triggers, @PFX@_synonyms
-- license: none
SELECT 'DEPENDENT' AS kind, d.owner, d.name, d.type AS detail, o.status
  FROM @PFX@_dependencies d
  LEFT JOIN @PFX@_objects o ON o.owner = d.owner AND o.object_name = d.name AND o.object_type = d.type
 WHERE d.referenced_owner = :owner
   AND d.referenced_name = :name
   AND NOT (d.owner = :owner AND d.name = :name)
UNION ALL
SELECT 'FK', c.owner, c.table_name, c.constraint_name, c.status
  FROM @PFX@_constraints c
  JOIN @PFX@_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name
 WHERE c.constraint_type = 'R'
   AND p.owner = :owner
   AND p.table_name = :name
UNION ALL
SELECT 'TRIGGER', t.owner, t.trigger_name, t.triggering_event, t.status
  FROM @PFX@_triggers t
 WHERE t.table_owner = :owner
   AND t.table_name = :name
UNION ALL
SELECT 'SYNONYM', s.owner, s.synonym_name, CAST(NULL AS VARCHAR2(128)), CAST(NULL AS VARCHAR2(8))
  FROM @PFX@_synonyms s
 WHERE s.table_owner = :owner
   AND s.table_name = :name
 ORDER BY 1, 2, 3
