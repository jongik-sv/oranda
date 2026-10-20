-- 객체 검색(11g 변형: FETCH FIRST 대신 ROWNUM 감싸기). 바인드는 object_search.sql 과 같다.
-- id: dbinfo.object_search
-- min-version: 11
-- requires: dba_objects, all_objects, dba_segments
-- license: none
SELECT owner, object_name, object_type, status, created, last_ddl_time, size_bytes
  FROM (SELECT owner, object_name, object_type, status, created, last_ddl_time, size_bytes
          FROM (SELECT o.owner, o.object_name, o.object_type, o.status, o.created, o.last_ddl_time, @SIZE@ AS size_bytes
                  FROM @PFX@_objects o
                 WHERE (:owner IS NULL OR o.owner = :owner)
                   AND (:types IS NULL OR INSTR(:types, '|' || o.object_type || '|') > 0)
                   AND (:name_pattern IS NULL OR o.object_name LIKE :name_pattern ESCAPE '\')
                   AND (:invalid_only = 0 OR o.status <> 'VALID')
                   AND (:created_after IS NULL OR o.created > :created_after)
                   AND (:modified_after IS NULL OR o.last_ddl_time > :modified_after)
                   AND o.object_type NOT IN ('LOB', 'TABLE PARTITION', 'TABLE SUBPARTITION', 'INDEX PARTITION', 'INDEX SUBPARTITION',
                                             'LOB PARTITION', 'JAVA CLASS', 'JAVA RESOURCE', 'JAVA SOURCE')
                   AND o.object_name NOT LIKE 'BIN$%')
         WHERE (:min_size IS NULL OR size_bytes >= :min_size)
           AND (:max_size IS NULL OR size_bytes <= :max_size)
         ORDER BY owner, object_type, object_name)
 WHERE ROWNUM <= :max_rows
