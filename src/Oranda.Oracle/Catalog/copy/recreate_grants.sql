-- 표 다시 만들기: 대상 표에 대상 계정이 직접 부여한 권한. 열 = GRANTEE, PRIVILEGE, GRANTABLE. 바인드: OWN, TBL, OWN.
-- ALL_TAB_PRIVS 의 TYPE 열은 12c 부터 있다(11.2 는 ORA-00904). 한 계정 안에서 표 이름은 다른 객체와 겹치지 않으므로 이름으로만 거른다.
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.recreate_grants
-- min-version: 11
-- requires: all_tab_privs
-- license: none
SELECT grantee, privilege, grantable FROM all_tab_privs WHERE table_schema = :OWN AND table_name = :TBL AND grantor = :OWN ORDER BY grantee, privilege
