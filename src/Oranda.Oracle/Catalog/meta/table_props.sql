-- 테이블 종류·테이블스페이스(테이블 편집기 TE-29). 바인드: owner, name. 뷰는 행이 없다(일반 표로 본다).
-- 열: tablespace_name(파티션·임시·IOT 는 NULL), temporary(Y/N), duration(SYS$SESSION·SYS$TRANSACTION), iot_type(IOT·IOT_OVERFLOW·IOT_MAPPING),
-- partitioned(YES/NO), cluster_name, external(YES/NO). 11g 의 all_tables 에는 external 열이 없어 all_external_tables 로 판정한다(두 판 같은 문장).
-- 저장 속성(테이블 편집기 「저장」 페이지 18.2, T13c): pct_free, ini_trans, compression(ENABLED/DISABLED), compress_for(BASIC·ADVANCED·OLTP·…), logging(YES/NO),
-- degree(공백 붙은 글, DEFAULT). 파티션 테이블은 degree 말고는 테이블 줄에 없어 NULL(「알 수 없음」, 파티션 기본값 all_part_tables.def_* 는 읽지 않음).
-- 익스텐트는 읽지 않는다(기존 테이블에 쓰지 않음 TE-39, 지연 세그먼트면 NULL).
-- id: meta.table_props
-- min-version: 11
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.tablespace_name, t.temporary, t.duration, t.iot_type, t.partitioned, t.cluster_name,
       CASE WHEN EXISTS (SELECT 1 FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name)
            THEN 'YES' ELSE 'NO' END AS external,
       t.pct_free, t.ini_trans, t.compression, t.compress_for, t.logging, t.degree
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
