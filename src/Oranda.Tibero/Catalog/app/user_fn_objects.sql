-- AI 사용자 함수 확인(UserFunctionSafetyChecker): 호출 이름이 가리키는 PL/SQL 객체. 열 = owner, object_name, object_type.
-- Tibero 도 Oracle 판과 같은 글이다(2026-11-23 Tibero 6 실측: 열·REGEXP_LIKE 같음).
-- @PAIRS@ = (owner = 'X' AND object_name = 'Y') 를 OR 로 이은 조건(이름은 식별자 문자만, 호출자가 만든다).
-- id: app.user_fn_objects
-- dbms: tibero
-- min-version: 6
-- requires: all_objects
-- license: none
SELECT owner, object_name, object_type FROM all_objects WHERE object_type IN ('FUNCTION','PROCEDURE','PACKAGE','TYPE') AND (@PAIRS@)
