-- 프로시저·함수 인자. 위치 0 의 이름 없는 행 = 함수 반환값. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- Oracle 과 달라진 점: Tibero 에는 dba_arguments 가 없어 인자는 @PFX@ 없이 all_arguments 만 쓴다(접속 사용자가 볼 수 있는 객체).
--   멤버 순번(m)은 @PFX@_procedures 에서 만든다. 인자 없는 서브프로그램은 all_arguments 에 행이 없어서 인자 쪽만으로 순번을 매기면 어긋난다.
-- data_level 열이 없어(중첩 인자 없음) 조건을 뺐고, defaulted 열이 없어 NULL(기본값 여부 모름), sequence 열이 없어 position 으로 정렬한다.
-- 열 overload: Tibero all_arguments.overload 는 멤버 순번(= *_PROCEDURES.MEMBER_NO)이다. procedures.sql 과 같은 규칙(같은 이름이 둘 이상이면 1..n, 하나면 NULL)으로
--   바꿔 낸다. 소비자는 (procedure_name, overload) 로 멤버와 묶는다. 맨 끝 열 subprogram_id 는 그 멤버 순번(원래 all_arguments.overload) — Oracle 판에는 없는 열.
-- id: meta.arguments
-- dbms: tibero
-- min-version: 6
-- requires: all_arguments, dba_procedures, all_procedures
-- license: none
WITH m AS (
  SELECT p.owner, p.object_name, p.procedure_name, p.member_no,
         CASE WHEN COUNT(*) OVER (PARTITION BY p.owner, p.object_name, p.procedure_name) > 1
              THEN TO_CHAR(ROW_NUMBER() OVER (PARTITION BY p.owner, p.object_name, p.procedure_name ORDER BY p.member_no))
         END AS overload
    FROM @PFX@_procedures p
   WHERE p.owner = :owner
     AND p.object_name = :name)
SELECT a.object_name AS procedure_name, m.overload, a.position, a.argument_name, a.in_out, a.data_type,
       CAST(NULL AS VARCHAR2(1)) AS defaulted,
       a.type_owner, a.type_name, a.type_subname,
       a.overload AS subprogram_id
  FROM all_arguments a
  LEFT JOIN m
    ON m.object_name = NVL(a.package_name, a.object_name)
   AND (m.procedure_name = a.object_name OR (m.procedure_name IS NULL AND a.package_name IS NULL))
   AND m.member_no = a.overload
 WHERE a.owner = :owner
   AND (a.package_name = :name OR (a.package_name IS NULL AND a.object_name = :name))
 ORDER BY a.object_name, a.overload, a.position
