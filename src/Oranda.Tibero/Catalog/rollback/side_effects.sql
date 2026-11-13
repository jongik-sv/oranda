-- 되돌리기 지점 확인 창 경고(설계 10.1): 대상 표의 켜진 트리거와, 이 표의 행을 지우면 함께 바뀌는 자식 표(ON DELETE CASCADE·SET NULL).
-- Oracle 판과 같은 문장·열(RB-10a). Tibero 6.2.15 실측: ALL_TRIGGERS·ALL_CONSTRAINTS.DELETE_RULE 이 같은 이름(docs/design/tibero-driver-exec.md 10장).
-- 열 = KIND('TRIGGER' | 'CASCADE' | 'SET NULL'), NAME('OWNER.NAME'). 바인드: owner, name(시노님을 푼 실제 표).
-- id: rollback.side_effects
-- dbms: tibero
-- min-version: 6
-- requires: all_triggers, all_constraints
-- license: none
SELECT 'TRIGGER' AS KIND, OWNER || '.' || TRIGGER_NAME AS NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :owner AND TABLE_NAME = :name AND STATUS = 'ENABLED'
UNION ALL
SELECT DISTINCT c.DELETE_RULE AS KIND, c.OWNER || '.' || c.TABLE_NAME AS NAME FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND c.DELETE_RULE IN ('CASCADE', 'SET NULL') AND p.OWNER = :owner AND p.TABLE_NAME = :name
ORDER BY 1, 2
