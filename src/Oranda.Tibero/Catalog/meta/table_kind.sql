-- 테이블 종류와 테이블스페이스(테이블 편집기 TE-29: TableInfo.Kind·Tablespace). 바인드: owner, name. 뷰·없는 테이블은 0행.
-- 열: tablespace_name(파티션 테이블은 NULL), temporary(YES/NO), duration(GTT 의 TRANSACTION/SESSION), iot_type(IOT), partitioned(YES/NO),
-- external_count(외부 테이블이면 1). Tibero *_tables 에 cluster_name 이 없어 클러스터 판정은 못 한다(dbinfo.column_reorder_traits 와 같음).
-- 실측 tibero-table-edit-probe.md 12·17장.
-- id: meta.table_kind
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.tablespace_name, t.temporary, t.duration, t.iot_type, t.partitioned,
       (SELECT COUNT(*) FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name) AS external_count
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
