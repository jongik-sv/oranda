-- 복사 사전 점검: 접속 계정(프록시 접속이면 프로필의 사용자 이름과 다르다). 열 = USER. 바인드 없음.
-- id: copy.session_user
-- min-version: 11
-- requires: dual
-- license: none
SELECT USER FROM DUAL
