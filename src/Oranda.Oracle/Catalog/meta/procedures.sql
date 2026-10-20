-- 패키지 멤버 또는 단독 프로시저·함수 목록. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- id: meta.procedures
-- min-version: 11
-- requires: dba_procedures, all_procedures
-- license: none
SELECT p.procedure_name, p.overload, p.subprogram_id, p.object_type
  FROM @PFX@_procedures p
 WHERE p.owner = :owner
   AND p.object_name = :name
   AND (p.procedure_name IS NOT NULL OR p.object_type IN ('PROCEDURE', 'FUNCTION'))
 ORDER BY p.subprogram_id
