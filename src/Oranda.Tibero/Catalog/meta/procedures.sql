-- 패키지 멤버 또는 단독 프로시저·함수 목록. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- Oracle 과 달라진 점: Tibero *_PROCEDURES 에는 overload·subprogram_id·object_type 열이 없다.
--   overload = Oracle 과 같은 뜻: 같은 소유자·패키지·이름의 서브프로그램이 둘 이상이면 선언 순서(member_no)대로 '1'..'n', 하나뿐이면 NULL.
--     (Tibero *_ARGUMENTS.OVERLOAD 는 이 번호가 아니라 member_no 라서, arguments.sql 이 *_PROCEDURES 와 맞춰 같은 규칙으로 다시 만든다 — 두 파일의 규칙을 같이 바꿀 것.)
--   subprogram_id = member_no(소유 객체 안의 선언 순번, 서브프로그램마다 고유; arguments.sql 의 subprogram_id 와 같은 값),
--   object_type = *_OBJECTS 의 소유 객체 유형(PACKAGE / PROCEDURE / FUNCTION / TYPE, 본문 BODY 행은 제외)
-- id: meta.procedures
-- dbms: tibero
-- min-version: 6
-- requires: dba_procedures, all_procedures, dba_objects, all_objects
-- license: none
SELECT p.procedure_name,
       CASE WHEN COUNT(*) OVER (PARTITION BY p.owner, p.object_name, p.procedure_name) > 1
            THEN TO_CHAR(ROW_NUMBER() OVER (PARTITION BY p.owner, p.object_name, p.procedure_name ORDER BY p.member_no))
       END AS overload,
       p.member_no AS subprogram_id, o.object_type
  FROM @PFX@_procedures p
  JOIN @PFX@_objects o
    ON o.owner = p.owner
   AND o.object_name = p.object_name
   AND o.object_type IN ('PACKAGE', 'PROCEDURE', 'FUNCTION', 'TYPE')
 WHERE p.owner = :owner
   AND p.object_name = :name
   AND (p.procedure_name IS NOT NULL OR o.object_type IN ('PROCEDURE', 'FUNCTION'))
 ORDER BY p.member_no
