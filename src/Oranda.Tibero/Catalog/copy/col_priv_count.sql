-- 복사 사전 점검: 표에 준 열 권한 수(DROP 과 함께 사라지고 되살리지 않는다). 열 = 건수 하나. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_col_privs 에는 table_schema 가 없고 소유자 열이 owner 다(TM0b 실측 -8026). 나머지 열은 IT 로 확인한다.
-- id: copy.col_priv_count
-- dbms: tibero
-- min-version: 6
-- requires: all_col_privs
-- license: none
SELECT COUNT(*) FROM all_col_privs WHERE owner = :OWN AND table_name = :TBL
