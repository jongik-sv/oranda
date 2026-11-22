-- AI 사용자 함수 확인: 소스의 첫 줄(래핑 확인)과 자치 트랜잭션·외부 언어 낱말이 든 줄. 열 = owner, name, type, line, text.
-- 명세(PACKAGE·TYPE)도 읽는다: 외부 언어 호출 명세는 명세에 적힐 수 있다. @PAIRS@ = (owner = 'X' AND name = 'Y') 를 OR 로 이은 조건.
-- id: app.user_fn_source
-- min-version: 11
-- requires: all_source
-- license: none
SELECT owner, name, type, line, text FROM all_source WHERE type IN ('FUNCTION','PROCEDURE','PACKAGE','PACKAGE BODY','TYPE','TYPE BODY','TRIGGER') AND (@PAIRS@) AND (line = 1 OR REGEXP_LIKE(text, 'AUTONOMOUS_TRANSACTION|LANGUAGE|EXTERNAL|MLE|SQL_MACRO|POLYMORPHIC', 'i'))
