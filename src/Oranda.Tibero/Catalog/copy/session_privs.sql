-- 복사 사전 점검: 이 세션의 시스템 권한(롤을 거쳐 받은 것 포함). 열 = PRIVILEGE. 바인드 없음. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.session_privs
-- dbms: tibero
-- min-version: 6
-- requires: session_privs
-- license: none
SELECT PRIVILEGE FROM SESSION_PRIVS
