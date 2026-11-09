-- 복사 사전 점검: 이 세션의 시스템 권한(롤을 거쳐 받은 것 포함, USER_SYS_PRIVS 의 상위 집합). 열 = PRIVILEGE. 바인드 없음.
-- id: copy.session_privs
-- min-version: 11
-- requires: session_privs
-- license: none
SELECT PRIVILEGE FROM SESSION_PRIVS
