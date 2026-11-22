-- AI 사용자 함수 확인: 객체가 기대는 것. 열 = 소유자, 이름, 종류, DB 링크(없으면 NULL). 열 순서는 Oracle 판과 같다.
-- Oracle 과 달라진 점: Tibero *_dependencies 의 참조 대상 열은 parent_obj_owner/parent_obj_name/parent_obj_type 이고 referenced_link_name 이 없다.
-- 원격 의존의 표기는 실측하지 못했다(링크 권한 없음) — 동의어처럼 이름 끝에 @링크 가 붙으면(뒤에 큰따옴표 없음) 그 뒤를 링크로 낸다(닫힌 쪽).
-- @PAIRS@ = (owner = 'X' AND name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_dependencies
-- dbms: tibero
-- min-version: 6
-- requires: all_dependencies
-- license: none
SELECT DISTINCT parent_obj_owner, parent_obj_name, parent_obj_type,
       CASE WHEN INSTR(parent_obj_name, '@', -1) > 0
             AND INSTR(SUBSTR(parent_obj_name, INSTR(parent_obj_name, '@', -1) + 1), '"') = 0
            THEN SUBSTR(parent_obj_name, INSTR(parent_obj_name, '@', -1) + 1) END AS link_name
  FROM all_dependencies WHERE (@PAIRS@)
