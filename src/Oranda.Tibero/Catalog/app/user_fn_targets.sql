-- AI 사용자 함수 확인: 동의어 대상의 종류. 열 = owner, object_name, object_type.
-- Tibero 도 Oracle 판과 같은 글이다(2026-11-23 Tibero 6 실측: 열·REGEXP_LIKE 같음).
-- @PAIRS@ = (owner = 'X' AND object_name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_targets
-- dbms: tibero
-- min-version: 6
-- requires: all_objects
-- license: none
SELECT owner, object_name, object_type FROM all_objects WHERE object_type NOT IN ('PACKAGE BODY','TYPE BODY') AND (@PAIRS@)
