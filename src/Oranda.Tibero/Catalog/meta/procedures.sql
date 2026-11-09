-- 패키지 멤버 또는 단독 프로시저·함수 목록. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- Oracle 과 달라진 점: Tibero *_PROCEDURES 에는 overload·subprogram_id·object_type 열이 없다.
--   overload = NULL 상수(Tibero 는 오버로드 번호를 주지 않음), subprogram_id = member_no(소유 객체 안의 순번, 정렬용),
--   object_type = *_OBJECTS 의 소유 객체 유형(PACKAGE / PROCEDURE / FUNCTION / TYPE, 본문 BODY 행은 제외)
-- id: meta.procedures
-- dbms: tibero
-- min-version: 6
-- requires: dba_procedures, all_procedures, dba_objects, all_objects
-- license: none
SELECT p.procedure_name, CAST(NULL AS VARCHAR2(40)) AS overload, p.member_no AS subprogram_id, o.object_type
  FROM @PFX@_procedures p
  JOIN @PFX@_objects o
    ON o.owner = p.owner
   AND o.object_name = p.object_name
   AND o.object_type IN ('PACKAGE', 'PROCEDURE', 'FUNCTION', 'TYPE')
 WHERE p.owner = :owner
   AND p.object_name = :name
   AND (p.procedure_name IS NOT NULL OR o.object_type IN ('PROCEDURE', 'FUNCTION'))
 ORDER BY p.member_no
