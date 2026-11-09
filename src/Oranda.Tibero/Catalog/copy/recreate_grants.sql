-- 표 다시 만들기: 대상 표에 대상 계정이 직접 부여한 권한. 열 = GRANTEE, PRIVILEGE, GRANTABLE('YES'/'NO'). 바인드: OWN, TBL, OWN(같은 이름 두 번 = 매개변수 하나, TM0b 3.8).
-- Oracle 판과 달라진 점: Tibero 의 all_tab_privs 에는 table_schema 가 없고 소유자 열이 owner 다(-8026, copy.table_privs 와 같다).
-- grantable 은 Oracle 과 같은 'YES'/'NO' 로 맞춰 돌려준다(Tibero 사전 뷰 일부는 'Y'/'N' 을 쓴다 — 어느 쪽이 와도 같은 뜻이 되게).
-- id: copy.recreate_grants
-- dbms: tibero
-- min-version: 6
-- requires: all_tab_privs
-- license: none
SELECT grantee, privilege, CASE WHEN grantable IN ('YES', 'Y') THEN 'YES' ELSE 'NO' END AS grantable FROM all_tab_privs WHERE owner = :OWN AND table_name = :TBL AND grantor = :OWN ORDER BY grantee, privilege
