-- 객체 이름 목록(유형·이름 패턴·상태·시각 필터). 바인드: owner, types('|TABLE|VIEW|' 형식), name_pattern, invalid_only(0/1), created_after, modified_after, max_rows
-- id: meta.objects
-- min-version: 19
-- requires: dba_objects, all_objects
-- license: none
SELECT o.owner, o.object_name, o.object_type, o.status, o.last_ddl_time, o.created
  FROM @PFX@_objects o
 WHERE (:owner IS NULL OR o.owner = :owner)
   AND (:types IS NULL OR INSTR(:types, '|' || o.object_type || '|') > 0)
   AND (:name_pattern IS NULL OR o.object_name LIKE :name_pattern ESCAPE '\')
   AND (:invalid_only = 0 OR o.status <> 'VALID')
   AND (:created_after IS NULL OR o.created > :created_after)
   AND (:modified_after IS NULL OR o.last_ddl_time > :modified_after)
   AND o.object_type NOT IN ('LOB', 'TABLE PARTITION', 'TABLE SUBPARTITION', 'INDEX PARTITION', 'INDEX SUBPARTITION',
                             'LOB PARTITION', 'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE')
   AND o.object_name NOT LIKE 'BIN$%'
 ORDER BY o.owner, o.object_type, o.object_name
 FETCH FIRST :max_rows ROWS ONLY
