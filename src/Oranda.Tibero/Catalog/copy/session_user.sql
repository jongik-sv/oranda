-- 복사 사전 점검: 접속 계정. 열 = USER. 바인드 없음. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.session_user
-- dbms: tibero
-- min-version: 6
-- requires: dual
-- license: none
SELECT USER FROM DUAL
