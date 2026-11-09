-- 프로시저·함수 인자. 위치 0 의 이름 없는 행 = 함수 반환값. 바인드: owner, name(패키지 또는 단독 객체 이름)
-- Oracle 과 달라진 점: Tibero 에는 dba_arguments 가 없어 @PFX@ 없이 all_arguments 만 쓴다(접속 사용자가 볼 수 있는 객체).
-- data_level 열이 없어(중첩 인자 없음) 조건을 뺐고, defaulted 열이 없어 NULL(기본값 여부 모름), sequence 열이 없어 position 으로 정렬한다.
-- id: meta.arguments
-- dbms: tibero
-- min-version: 6
-- requires: all_arguments
-- license: none
SELECT a.object_name AS procedure_name, a.overload, a.position, a.argument_name, a.in_out, a.data_type,
       CAST(NULL AS VARCHAR2(1)) AS defaulted,
       a.type_owner, a.type_name, a.type_subname
  FROM all_arguments a
 WHERE a.owner = :owner
   AND (a.package_name = :name OR (a.package_name IS NULL AND a.object_name = :name))
 ORDER BY a.object_name, a.overload, a.position
