-- 이 세션의 SID(컴파일을 막는 세션을 다른 연결에서 찾을 때 쓴다, plsql.compile_blocker 의 :sid). 컴파일 세션이 CREATE 를 보내기 전에 한 번 읽어 둔다.
-- id: plsql.session_sid
-- min-version: 11
-- license: none
SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')) AS sid FROM DUAL