-- 복사 사전 점검: 이 세션에 켜진 롤. 열 = ROLE. 바인드 없음. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.session_roles
-- dbms: tibero
-- min-version: 6
-- requires: session_roles
-- license: none
SELECT ROLE FROM SESSION_ROLES
