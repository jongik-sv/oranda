-- 이름 풀이 한 단계(시노님 따라가기, TSK-13-44). 바인드: owner, name. 그 스키마의 표·뷰(OBJECT)가 먼저, 아니면 시노님(SYNONYM)이 가리키는 소유자·이름·DB 링크. 없으면 행 없음.
-- id: meta.name_step
-- min-version: 11
-- requires: dba_synonyms, all_synonyms, dba_objects, all_objects
-- license: none
SELECT kind, table_owner, table_name, db_link
  FROM (SELECT 'OBJECT' AS kind, CAST(NULL AS VARCHAR2(128)) AS table_owner, CAST(NULL AS VARCHAR2(128)) AS table_name,
               CAST(NULL AS VARCHAR2(128)) AS db_link, 0 AS ord
          FROM @PFX@_objects o
         WHERE o.owner = :owner AND o.object_name = :name
           AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
        UNION ALL
        SELECT 'SYNONYM', s.table_owner, s.table_name, s.db_link, 1
          FROM @PFX@_synonyms s
         WHERE s.owner = :owner AND s.synonym_name = :name)
 ORDER BY ord
