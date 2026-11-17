-- 인덱스와 열. 바인드: owner, name
-- 그 뒤 열(테이블 편집기 TE-29): descend(ASC/DESC), column_expression(함수 기반·DESC 열의 식, LONG — InitialLONGFetchSize = -1), tablespace_name.
-- DESC 열은 서버가 숨은 열(SYS_NC…)로 만들어 column_name 이 그 이름이고 식이 "열" 이다(docs/design/oracle-table-edit-probe.md 10).
-- id: meta.indexes
-- min-version: 11
-- requires: dba_indexes, all_indexes, dba_ind_columns, all_ind_columns, dba_ind_expressions, all_ind_expressions
-- license: none
SELECT i.index_name, i.uniqueness, i.index_type, ic.column_name, ic.column_position,
       ic.descend, ie.column_expression, i.tablespace_name
  FROM @PFX@_indexes i
  JOIN @PFX@_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
  LEFT JOIN @PFX@_ind_expressions ie
    ON ie.index_owner = ic.index_owner AND ie.index_name = ic.index_name AND ie.column_position = ic.column_position
 WHERE i.table_owner = :owner
   AND i.table_name = :name
 ORDER BY i.index_name, ic.column_position
