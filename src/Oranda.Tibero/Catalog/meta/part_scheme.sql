-- 파티션 방식·키 열·INTERVAL(테이블 편집기 T14, 설계 18.4). 바인드: owner, name. 파티션 테이블이 아니면 0행.
-- 한 행 = 키 열 하나: level_kind = PART(파티션 키) | SUB(하위 파티션 키), 같은 테이블의 방식·하위 방식·INTERVAL 은 모든 행에 같다.
-- Oracle 판과 열 이름·순서가 같다(Tibero 의 *_part_tables·*_part_key_columns·*_subpart_key_columns 열 이름이 같음, 실측 tibero-table-edit-probe.md 11장).
-- id: meta.part_scheme
-- dbms: tibero
-- min-version: 6
-- requires: dba_part_tables, all_part_tables, dba_part_key_columns, all_part_key_columns, dba_subpart_key_columns, all_subpart_key_columns
-- license: none
SELECT pt.partitioning_type, pt.subpartitioning_type, pt.interval, 'PART' AS level_kind, k.column_name, k.column_position
  FROM @PFX@_part_tables pt
  JOIN @PFX@_part_key_columns k ON k.owner = pt.owner AND k.name = pt.table_name AND k.object_type = 'TABLE'
 WHERE pt.owner = :owner
   AND pt.table_name = :name
UNION ALL
SELECT pt.partitioning_type, pt.subpartitioning_type, pt.interval, 'SUB', k.column_name, k.column_position
  FROM @PFX@_part_tables pt
  JOIN @PFX@_subpart_key_columns k ON k.owner = pt.owner AND k.name = pt.table_name AND k.object_type = 'TABLE'
 WHERE pt.owner = :owner
   AND pt.table_name = :name
 ORDER BY 4, 6
