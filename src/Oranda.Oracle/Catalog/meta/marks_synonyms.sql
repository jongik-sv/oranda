-- 스키마 브라우저 동의어 표식(대상 종류·깨진 동의어·DB 링크). 바인드: owner.
-- 열(ObjectMarkRows.Synonym): synonym_name, db_link, target_type(대상 객체의 유형, 못 찾으면 NULL = 깨짐).
-- 비용: 동의어마다 대상을 객체 뷰에서 찾는다. PUBLIC 동의어(수천 개)는 읽지 않는다(owner <> 'PUBLIC') — 그 스키마가 가진 동의어만.
-- all_ 뷰(사전 권한 없음)는 볼 권한이 없는 대상도 「못 찾음」으로 나온다. 동의어 → 동의어 사슬은 한 단계만 본다(대상이 동의어면 SYNONYM).
-- 대상 유형은 같은 이름의 다른 이름공간 객체(인덱스·트리거·본문·파티션)를 빼고 고른다. DB 링크를 거치는 동의어는 대상을 찾지 않는다.
-- id: meta.marks_synonyms
-- min-version: 11
-- requires: dba_synonyms, all_synonyms, dba_objects, all_objects
-- license: none
SELECT s.synonym_name, s.db_link,
       CASE WHEN s.db_link IS NULL THEN
         (SELECT MIN(o.object_type)
            FROM @PFX@_objects o
           WHERE o.owner = s.table_owner
             AND o.object_name = s.table_name
             AND o.object_type NOT IN ('INDEX', 'INDEX PARTITION', 'INDEX SUBPARTITION', 'TABLE PARTITION', 'TABLE SUBPARTITION',
                                       'LOB', 'LOB PARTITION', 'LOB SUBPARTITION', 'TRIGGER', 'PACKAGE BODY', 'TYPE BODY'))
       END AS target_type
  FROM @PFX@_synonyms s
 WHERE s.owner = :owner
   AND s.owner <> 'PUBLIC'
