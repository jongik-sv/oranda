-- AI 사용자 함수 확인(UserFunctionSafetyChecker): 호출 이름이 가리키는 PL/SQL 객체. 열 = owner, object_name, object_type.
-- @PAIRS@ = (owner = 'X' AND object_name = 'Y') 를 OR 로 이은 조건(이름은 식별자 문자만, 호출자가 만든다).
-- id: app.user_fn_objects
-- min-version: 11
-- requires: all_objects
-- license: none
SELECT owner, object_name, object_type FROM all_objects WHERE object_type IN ('FUNCTION','PROCEDURE','PACKAGE','TYPE') AND (@PAIRS@)
