-- 복사 사전 점검: 소유자가 아닌 계정이 표에 준 객체 권한 수. 열 = 건수 하나. 바인드: OWN(두 자리에 같은 값), TBL.
-- 권한 종류(TYPE) 열은 12c 부터라 쓰지 않는다(11.2 에 없다).
-- id: copy.other_grantor_count
-- min-version: 11
-- requires: all_tab_privs
-- license: none
SELECT COUNT(*) FROM all_tab_privs WHERE table_schema = :OWN AND table_name = :TBL AND grantor <> :OWN
