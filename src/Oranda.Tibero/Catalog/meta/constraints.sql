-- 제약(기본키·유일·외래키·검사). NOT NULL 검사 제약(조건 전체가 `"열" IS NOT NULL`)은 열 정보에 있으므로 읽는 쪽(MetadataRows.BuildConstraints)이 뺀다. 바인드: owner, name
-- SQL 에서 `LIKE '%IS NOT NULL'` 로 거르면 `a > 0 OR b IS NOT NULL` 처럼 끝만 같은 사용자 CHECK 까지 빠진다(형식 변환 5단계가 되살리지 못함, T17b) → 거르지 않는다.
-- Oracle 과 달라진 점: search_condition_vc 열이 없어 search_condition 을 쓴다(결과 열 이름도 search_condition, Oracle 11g 변형과 같다).
-- 위치 보정: Tibero *_CONS_COLUMNS.POSITION 은 0부터(실측 6.2.15) → 내보내는 position 만 +1(Oracle 과 같은 1부터), 조인·정렬은 원래 값.
-- 끝 열(테이블 편집기 TE-29): delete_rule(FK 의 CASCADE/SET NULL/NO ACTION), r_column_name(같은 위치의 참조 열), index_name(PK·UK 가 쓰는 인덱스),
-- validated(VALIDATED/NOT VALIDATED), generated(GENERATED NAME = 서버가 지은 이름 <계정>_CON<n>, USER NAME). 앞 9열의 뜻·순서는 그대로.
-- id: meta.constraints
-- dbms: tibero
-- min-version: 6
-- requires: dba_constraints, all_constraints, dba_cons_columns, all_cons_columns
-- license: none
SELECT c.constraint_name, c.constraint_type, c.status, c.search_condition, c.r_owner, rc.table_name AS r_table_name,
       cc.column_name, cc.position + 1 AS position, c.r_constraint_name,
       CASE WHEN c.constraint_type = 'R' THEN c.delete_rule END AS delete_rule, rcc.column_name AS r_column_name,
       CASE WHEN c.constraint_type IN ('P', 'U') THEN c.index_name END AS index_name, c.validated, c.generated
  FROM @PFX@_constraints c
  LEFT JOIN @PFX@_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
  LEFT JOIN @PFX@_constraints rc ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
  LEFT JOIN @PFX@_cons_columns rcc
    ON rcc.owner = c.r_owner
   AND rcc.constraint_name = c.r_constraint_name
   AND rcc.position = cc.position
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.constraint_type IN ('P', 'U', 'R', 'C')
 ORDER BY c.constraint_name, cc.position
