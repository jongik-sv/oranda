-- 제약 하나의 열(위치·열 이름)과 외래키면 같은 위치의 참조 표·열. 바인드: owner, name, constraint_name (Oracle 판과 같은 뜻, 위치 보정만 다름)
-- 위치 보정: Tibero *_CONS_COLUMNS.POSITION 은 0부터(실측 6.2.15) → 내보내는 position 만 +1(Oracle 과 같은 1부터), 조인·정렬은 원래 값.
-- id: meta.constraint_columns
-- dbms: tibero
-- min-version: 6
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT cc.position + 1 AS position, cc.column_name, c.r_owner, rc.table_name AS r_table_name, rcc.column_name AS r_column_name
  FROM @PFX@_constraints c
  JOIN @PFX@_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
  LEFT JOIN @PFX@_constraints rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
  LEFT JOIN @PFX@_cons_columns rcc
    ON rcc.owner = c.r_owner
   AND rcc.constraint_name = c.r_constraint_name
   AND rcc.position = cc.position
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.constraint_name = :constraint_name
 ORDER BY cc.position, cc.column_name
