-- 복사(없으면 만들기, SQ2): 원본 테이블 권한을 받을 사용자가 대상 DB 에 있는가. 롤은 이 뷰로 보이지 않아 0 이면 실행 때 붙여 보고 실패하면 알린다. 열 = COUNT(*). 바인드: NAME.
-- id: copy.user_exists
-- dbms: tibero
-- min-version: 6
-- requires: all_users
-- license: none
SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = :NAME
