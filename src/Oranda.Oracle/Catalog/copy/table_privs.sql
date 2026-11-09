-- 복사 사전 점검: 표·시퀀스의 객체 권한(같은 뷰에 있다). 부여받은 대상(GRANTEE)은 호출한 쪽이 거른다. 열 = GRANTEE, PRIVILEGE.
-- 바인드: OWN(소유자), TBL(표 또는 시퀀스 이름).
-- id: copy.table_privs
-- min-version: 11
-- requires: all_tab_privs
-- license: none
SELECT GRANTEE, PRIVILEGE FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :OWN AND TABLE_NAME = :TBL
