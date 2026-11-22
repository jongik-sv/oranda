-- AI 사용자 함수 확인: 동의어 대상의 종류. 열 = owner, object_name, object_type.
-- @PAIRS@ = (owner = 'X' AND object_name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_targets
-- min-version: 11
-- requires: all_objects
-- license: none
SELECT owner, object_name, object_type FROM all_objects WHERE object_type NOT IN ('PACKAGE BODY','TYPE BODY') AND (@PAIRS@)
