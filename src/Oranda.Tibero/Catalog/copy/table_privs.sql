-- 복사 사전 점검: 표·시퀀스의 객체 권한(같은 뷰에 있다). 부여받은 대상(GRANTEE)은 호출한 쪽이 거른다. 열 = GRANTEE, PRIVILEGE.
-- 바인드: OWN(소유자), TBL(표 또는 시퀀스 이름).
-- Oracle 판과 달라진 점: Tibero 의 all_tab_privs 에는 table_schema 가 없고 소유자 열이 owner 다(TM0b 실측 -8026).
-- id: copy.table_privs
-- dbms: tibero
-- min-version: 6
-- requires: all_tab_privs
-- license: none
SELECT GRANTEE, PRIVILEGE FROM ALL_TAB_PRIVS WHERE OWNER = :OWN AND TABLE_NAME = :TBL
