-- 스키마 브라우저 테이블 표식(세부 종류·파티션). 바인드: owner. 일반(힙)·비파티션 테이블은 빼고 표식이 있는 테이블만 돌려준다. 열 순서는 Oracle 판과 같다(ObjectMarkRows.Table).
-- Tibero 에서 모르는 것(= 표식 없음): *_tables 에 cluster_name 이 없어 클러스터 테이블은 판정하지 못한다(cluster_name 은 NULL).
-- all_object_tables 가 없어(6.2.15 실측 「없는 객체」) 객체 테이블도 판정하지 못한다(is_object 는 늘 NO).
-- temporary 는 YES/NO, duration 은 SESSION·TRANSACTION(실측). 파티션은 파티션 테이블 줄에서만 스칼라 부질의로, 외부 테이블은 따로 UNION ALL 로 읽는다(Oracle 판과 같은 까닭).
-- id: meta.marks_tables
-- dbms: tibero
-- min-version: 6
-- requires: dba_tables, all_tables, dba_part_tables, all_part_tables, dba_external_tables, all_external_tables
-- license: none
SELECT t.table_name, t.temporary, t.duration, CASE WHEN t.iot_type = 'IOT' THEN 'IOT' END AS iot_type, NULL AS cluster_name,
       CASE WHEN t.partitioned = 'YES' THEN
         (SELECT p.partitioning_type || '|' || p.subpartitioning_type || '|' || NVL2(p.interval, 'YES', NULL)
            FROM @PFX@_part_tables p
           WHERE p.owner = t.owner
             AND p.table_name = t.table_name)
       END AS partitioning,
       'NO' AS is_external, 'NO' AS is_object
  FROM @PFX@_tables t
 WHERE t.owner = :owner
   AND (t.temporary = 'YES' OR t.iot_type = 'IOT' OR t.partitioned = 'YES')
UNION ALL
SELECT e.table_name, NULL, NULL, NULL, NULL, NULL, 'YES', 'NO'
  FROM @PFX@_external_tables e
 WHERE e.owner = :owner
