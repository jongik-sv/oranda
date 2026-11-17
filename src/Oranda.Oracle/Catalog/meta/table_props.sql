-- 테이블 종류·테이블스페이스(테이블 편집기 TE-29). 바인드: owner, name. 뷰는 행이 없다(일반 표로 본다).
-- 열: tablespace_name(파티션·임시·IOT 는 NULL), temporary(Y/N), duration(SYS$SESSION·SYS$TRANSACTION), iot_type(IOT·IOT_OVERFLOW·IOT_MAPPING),
-- partitioned(YES/NO), cluster_name, external(YES/NO). 11g 의 all_tables 에는 external 열이 없어 all_external_tables 로 판정한다(두 판 같은 문장).
-- id: meta.table_props
-- min-version: 11
-- requires: dba_tables, all_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.tablespace_name, t.temporary, t.duration, t.iot_type, t.partitioned, t.cluster_name,
       CASE WHEN EXISTS (SELECT 1 FROM @PFX@_external_tables e WHERE e.owner = t.owner AND e.table_name = t.table_name)
            THEN 'YES' ELSE 'NO' END AS external
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND t.table_name = :name
