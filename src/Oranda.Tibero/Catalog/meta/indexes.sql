-- 인덱스와 열. 바인드: owner, name. 앞 5열은 Oracle 판과 같다.
-- 끝 열(테이블 편집기 TE-29): descend(열별 ASC/DESC — Tibero 는 DESC 인덱스도 NORMAL·실제 열 이름), column_expression(함수 기반 식, 열 이름은
-- 「- EXPRESSION COLUMN -」), tablespace_name, constraint_name(이 인덱스를 쓰는 PK·UK 제약, 없으면 NULL). 실측 tibero-table-edit-probe.md 5·16장.
-- 그 뒤(스키마 브라우저 표식): status(VALID·UNUSABLE), partitioned(YES/NO). Tibero index_type 은 NORMAL·IOT - TOP·LOB 뿐이라(6.2.15 실측)
-- 비트맵·역방향은 모름(표식 없음), 함수 기반은 column_expression 으로 가른다.
-- id: meta.indexes
-- dbms: tibero
-- min-version: 6
-- requires: dba_indexes, all_indexes, dba_ind_columns, all_ind_columns, dba_ind_expressions, all_ind_expressions, dba_constraints, all_constraints
-- license: none
SELECT i.index_name, i.uniqueness, i.index_type, ic.column_name, ic.column_position,
       ic.descend, ie.column_expression, i.tablespace_name,
       (SELECT MIN(k.constraint_name)
          FROM @PFX@_constraints k
         WHERE k.owner = i.table_owner
           AND k.table_name = i.table_name
           AND k.constraint_type IN ('P', 'U')
           AND k.index_owner = i.owner
           AND k.index_name = i.index_name) AS constraint_name,
       i.status, i.partitioned
  FROM @PFX@_indexes i
  JOIN @PFX@_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
  LEFT JOIN @PFX@_ind_expressions ie
    ON ie.index_owner = ic.index_owner
   AND ie.index_name = ic.index_name
   AND ie.column_position = ic.column_position
 WHERE i.table_owner = :owner
   AND i.table_name = :name
 ORDER BY i.index_name, ic.column_position
