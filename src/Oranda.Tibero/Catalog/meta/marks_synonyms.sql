-- 스키마 브라우저 동의어 표식(대상 종류·깨진 동의어). 바인드: owner. 열 순서는 Oracle 판과 같다(ObjectMarkRows.Synonym).
-- Tibero *_synonyms 의 대상 열은 org_object_owner·org_object_name 이고 db_link 열이 없다(NULL = DB 링크 표식 없음).
-- PUBLIC 동의어는 읽지 않는다(owner <> 'PUBLIC', 6.2.15 시험 서버 약 980개) — 그 스키마가 가진 동의어만. 대상 유형은 Oracle 판과 같은 규칙으로 고른다.
-- id: meta.marks_synonyms
-- dbms: tibero
-- min-version: 6
-- requires: dba_synonyms, all_synonyms, dba_objects, all_objects
-- license: none
SELECT s.synonym_name, NULL AS db_link,
       (SELECT MIN(o.object_type)
          FROM @PFX@_objects o
         WHERE o.owner = s.org_object_owner
           AND o.object_name = s.org_object_name
           AND o.object_type NOT IN ('INDEX', 'INDEX PARTITION', 'INDEX SUBPARTITION', 'TABLE PARTITION', 'TABLE SUBPARTITION',
                                     'LOB', 'LOB PARTITION', 'LOB SUBPARTITION', 'TRIGGER', 'PACKAGE BODY', 'TYPE BODY')) AS target_type
  FROM @PFX@_synonyms s
 WHERE s.owner = :owner
   AND s.owner <> 'PUBLIC'
