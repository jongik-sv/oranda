-- 테이블 종류와 테이블스페이스(테이블 편집기 TE-29: TableInfo.Kind·Tablespace). 바인드: owner, name. 뷰·없는 테이블은 0행.
-- 열: tablespace_name(파티션 테이블은 NULL), temporary(YES/NO), duration(GTT 의 TRANSACTION/SESSION), iot_type(IOT), partitioned(YES/NO),
-- external_count(외부 테이블이면 1). Tibero *_tables 에 cluster_name 이 없어 클러스터 판정은 못 한다(dbinfo.column_reorder_traits 와 같음).
-- 실측 tibero-table-edit-probe.md 12·17장.
-- 저장 속성(「저장」 페이지 18.2, T13c): pct_free, ini_trans, compression(YES/NO), logging(YES/NO), degree(NOPARALLEL = 0, PARALLEL = -1, PARALLEL n = n).
-- 익스텐트는 읽지 않는다(기존 테이블에 쓰지 않음 TE-39, 사전 값은 최소 익스텐트로 올라가 입력과 다름: INITIAL 256K → 131072).
-- Tibero *_tables 에는 compress_for 가 없다(압축은 켜짐/꺼짐뿐).
-- id: meta.table_kind
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.tablespace_name, t.temporary, t.duration, t.iot_type, t.partitioned,
       (SELECT COUNT(*) FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name) AS external_count,
       t.pct_free, t.ini_trans, t.compression, t.logging, t.degree
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
