-- AI 사용자 함수 확인: 객체가 기대는 것. 열 = 소유자, 이름, 종류, DB 링크(없으면 NULL).
-- @PAIRS@ = (owner = 'X' AND name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_dependencies
-- min-version: 11
-- requires: all_dependencies
-- license: none
SELECT DISTINCT referenced_owner, referenced_name, referenced_type, referenced_link_name FROM all_dependencies WHERE (@PAIRS@)
