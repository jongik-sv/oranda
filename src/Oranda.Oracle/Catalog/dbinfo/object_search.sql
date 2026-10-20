-- 객체 검색(이름·유형·크기·생성일·변경일). 바인드: owner, types('|TABLE|VIEW|' 형식), name_pattern, invalid_only(0/1), created_after, modified_after, min_size, max_size, max_rows @SEGMENTS@ 는 호출 쪽이 dba_segments(DBA) 또는 USER 를 owner 로 붙인 user_segments 로 바꾼다.
-- id: dbinfo.object_search
-- min-version: 19
-- requires: dba_objects, all_objects, dba_segments
-- license: none
WITH seg AS (
       -- 세그먼트 크기는 한 번만 모아(GROUP BY) 붙인다. 객체마다 세그먼트 뷰를 다시 읽지 않는다(TSK-07-06).
       -- 유형은 TABLE·INDEX 계열(파티션·하위 파티션 포함)을 묶어 객체 유형과 맞춘다.
       SELECT g.owner, g.segment_name,
              CASE WHEN g.segment_type LIKE 'TABLE%' THEN 'TABLE'
                   WHEN g.segment_type LIKE 'INDEX%' THEN 'INDEX'
                   ELSE g.segment_type END AS object_type,
              SUM(g.bytes) AS bytes
         FROM @SEGMENTS@ g
        WHERE (:owner IS NULL OR g.owner = :owner)
        GROUP BY g.owner, g.segment_name,
                 CASE WHEN g.segment_type LIKE 'TABLE%' THEN 'TABLE'
                      WHEN g.segment_type LIKE 'INDEX%' THEN 'INDEX'
                      ELSE g.segment_type END)
SELECT o.owner, o.object_name, o.object_type, o.status, o.created, o.last_ddl_time, seg.bytes AS size_bytes
  FROM @PFX@_objects o
  LEFT JOIN seg ON seg.owner = o.owner AND seg.segment_name = o.object_name AND seg.object_type = o.object_type
 WHERE (:owner IS NULL OR o.owner = :owner)
   AND (:types IS NULL OR INSTR(:types, '|' || o.object_type || '|') > 0)
   AND (:name_pattern IS NULL OR o.object_name LIKE :name_pattern ESCAPE '\')
   AND (:invalid_only = 0 OR o.status <> 'VALID')
   AND (:created_after IS NULL OR o.created > :created_after)
   AND (:modified_after IS NULL OR o.last_ddl_time > :modified_after)
   AND o.object_type NOT IN ('LOB', 'TABLE PARTITION', 'TABLE SUBPARTITION', 'INDEX PARTITION', 'INDEX SUBPARTITION',
                             'LOB PARTITION', 'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE')
   AND o.object_name NOT LIKE 'BIN$%'
   AND (:min_size IS NULL OR seg.bytes >= :min_size)
   AND (:max_size IS NULL OR seg.bytes <= :max_size)
 ORDER BY o.owner, o.object_type, o.object_name
 FETCH FIRST :max_rows ROWS ONLY
