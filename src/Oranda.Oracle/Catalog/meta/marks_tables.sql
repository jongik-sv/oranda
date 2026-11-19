-- 스키마 브라우저 테이블 표식(세부 종류·파티션). 바인드: owner. 일반(힙)·비파티션 테이블은 빼고 표식이 있는 테이블만 돌려준다.
-- 열(ObjectMarkRows.Table): table_name, temporary(Y/N), duration(SYS$SESSION·SYS$TRANSACTION), iot_type(IOT 만), cluster_name,
-- partitioning(방식|하위 방식|간격이면 YES, 예 RANGE|NONE|YES), is_external(YES/NO), is_object(YES/NO). 같은 테이블이 두 갈래에 나오면 읽는 쪽이 합친다.
-- 가볍게(사전 권한 없는 all_ 뷰 실측 26ai): all_part_tables·all_external_tables 를 JOIN 하면 SYS 기준 2~4초가 걸려, 파티션은 파티션 테이블 줄에서만 스칼라 부질의로,
-- 외부 테이블은 따로 UNION ALL 로 읽는다(11g 의 all_tables 에는 external 열도 없다).
-- 객체 테이블(CREATE TABLE t OF 타입, XMLType 표 포함)은 all_tables 에 없고 all_object_tables 는 소유자 하나도 2초 넘게 걸려, 객체 테이블에만 있는 숨은 열
-- SYS_NC_ROWINFO$ 를 all_tab_cols 에서 찾는다. 객체 테이블의 임시·파티션 같은 덧붙은 속성은 읽지 않는다(종류만).
-- 객체 목록(meta.objects)과 따로, 목록을 보인 뒤 소유자마다 한 번 읽는다(CachedMetadataProvider.Marks).
-- id: meta.marks_tables
-- min-version: 11
-- requires: dba_tables, all_tables, dba_part_tables, all_part_tables, dba_external_tables, all_external_tables, dba_tab_cols, all_tab_cols
-- license: none
SELECT t.table_name, t.temporary, t.duration, CASE WHEN t.iot_type = 'IOT' THEN 'IOT' END AS iot_type, t.cluster_name,
       CASE WHEN t.partitioned = 'YES' THEN
         (SELECT p.partitioning_type || '|' || p.subpartitioning_type || '|' || NVL2(p.interval, 'YES', NULL)
            FROM @PFX@_part_tables p
           WHERE p.owner = t.owner
             AND p.table_name = t.table_name)
       END AS partitioning,
       'NO' AS is_external, 'NO' AS is_object
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND (t.temporary = 'Y' OR t.iot_type = 'IOT' OR t.cluster_name IS NOT NULL OR t.partitioned = 'YES')
UNION ALL
SELECT e.table_name, NULL, NULL, NULL, NULL, NULL, 'YES', 'NO'
  FROM @PFX@_external_tables e
 WHERE e.owner = :owner
UNION ALL
SELECT c.table_name, NULL, NULL, NULL, NULL, NULL, 'NO', 'YES'
  FROM @PFX@_tab_cols c
 WHERE c.owner = :owner
   AND c.column_name = 'SYS_NC_ROWINFO$'
