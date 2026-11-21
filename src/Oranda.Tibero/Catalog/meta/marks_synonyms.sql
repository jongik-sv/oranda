-- 스키마 브라우저 동의어 표식(대상 종류·깨진 동의어·DB 링크). 바인드: owner. 열 순서는 Oracle 판과 같다(ObjectMarkRows.Synonym).
-- Tibero *_synonyms 의 대상 열은 org_object_owner·org_object_name 이고 db_link 열이 없다. 원격 동의어는 org_object_name 에 "이름"@링크(따옴표 없이 이름@링크도)로
-- 적혀 있다(2026-11-21 Tibero 6 실측: PUBLIC 원격 동의어 3건 모두 "이름"@링크). 마지막 @ 뒤에 큰따옴표가 없으면 그 뒤를 db_link 로 내고 대상은 찾지 않는다
-- (Oracle 판과 같다 — 찾으면 「대상 없음」 = 깨짐으로 잘못 보인다). @ 가 없으면 지금처럼 대상을 객체 뷰에서 찾는다.
-- PUBLIC 동의어는 읽지 않는다(owner <> 'PUBLIC', 6.2.15 시험 서버 약 980개) — 그 스키마가 가진 동의어만. 대상 유형은 Oracle 판과 같은 규칙으로 고른다.
-- id: meta.marks_synonyms
-- dbms: tibero
-- min-version: 6
-- requires: dba_synonyms, all_synonyms, dba_objects, all_objects
-- license: none
SELECT x.synonym_name, x.db_link,
       CASE WHEN x.db_link IS NULL THEN
         (SELECT MIN(o.object_type)
            FROM @PFX@_objects o
           WHERE o.owner = x.org_object_owner
             AND o.object_name = x.org_object_name
             AND o.object_type NOT IN ('INDEX', 'INDEX PARTITION', 'INDEX SUBPARTITION', 'TABLE PARTITION', 'TABLE SUBPARTITION',
                                       'LOB', 'LOB PARTITION', 'LOB SUBPARTITION', 'TRIGGER', 'PACKAGE BODY', 'TYPE BODY'))
       END AS target_type
  FROM (SELECT s.synonym_name, s.org_object_owner, s.org_object_name,
               CASE WHEN INSTR(s.org_object_name, '@', -1) > 0
                     AND INSTR(SUBSTR(s.org_object_name, INSTR(s.org_object_name, '@', -1) + 1), '"') = 0
                    THEN SUBSTR(s.org_object_name, INSTR(s.org_object_name, '@', -1) + 1) END AS db_link
          FROM @PFX@_synonyms s
         WHERE s.owner = :owner
           AND s.owner <> 'PUBLIC') x
