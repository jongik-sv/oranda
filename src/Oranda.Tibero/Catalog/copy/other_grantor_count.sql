-- 복사 사전 점검: 소유자가 아닌 계정이 표에 준 객체 권한 수. 열 = 건수 하나. 바인드: OWN(두 자리에 같은 값), TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_tab_privs 에는 table_schema 가 없고 소유자 열이 owner 다(TM0b 실측 -8026).
-- id: copy.other_grantor_count
-- dbms: tibero
-- min-version: 6
-- requires: all_tab_privs
-- license: none
SELECT COUNT(*) FROM all_tab_privs WHERE owner = :OWN AND table_name = :TBL AND grantor <> :OWN
