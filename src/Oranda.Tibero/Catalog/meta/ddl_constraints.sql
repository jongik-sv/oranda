-- 표의 제약 전체(기본키·유일 키·검사·외래키) — 종속 DDL 의 ALTER TABLE … ADD CONSTRAINT 를 만든다. 바인드: owner, name(표). 한 줄 = 제약 열 하나.
-- NOT NULL 검사 제약도 그대로 낸다. meta.constraints 처럼 LIKE 로 거르지 않고 읽는 쪽이 조건 글 전체가 «열 하나 IS NOT NULL» 인 것만 뺀다.
-- 외래키는 같은 위치의 참조 열(rcc)과 참조 표·DELETE_RULE 을 함께 낸다. 쓰는 열은 meta.constraints·meta.constraint_columns·meta.detail_referenced_by 가
-- 이미 쓰는 열이다. VALIDATED·DEFERRABLE·DEFERRED 는 실측표(docs/design/tibero-catalog-map.md)에 없어 읽지 않는다(NOVALIDATE·DEFERRABLE 절은 못 만든다).
-- 위치 보정 *_CONS_COLUMNS.POSITION 은 Tibero 에서 0부터(실측 6.2.15)라 내보내는 position 만 +1, 조인·정렬은 원래 값(열 순서는 그대로).
-- id: meta.ddl_constraints
-- dbms: tibero
-- min-version: 6
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT c.constraint_name, c.constraint_type, c.status, c.search_condition, cc.position + 1 AS position, cc.column_name,
       c.r_owner, rc.table_name AS r_table_name, rcc.column_name AS r_column_name, c.delete_rule
  FROM @PFX@_constraints c
  LEFT JOIN @PFX@_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
  LEFT JOIN @PFX@_constraints rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
  LEFT JOIN @PFX@_cons_columns rcc
    ON rcc.owner = c.r_owner
   AND rcc.constraint_name = c.r_constraint_name
   AND rcc.position = cc.position
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.constraint_type IN ('P', 'U', 'C', 'R')
 ORDER BY c.constraint_name, cc.position
