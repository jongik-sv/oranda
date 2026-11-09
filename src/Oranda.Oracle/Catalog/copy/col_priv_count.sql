-- 복사 사전 점검: 표에 준 열 권한 수(DROP 과 함께 사라지고 되살리지 않는다). 열 = 건수 하나. 바인드: OWN, TBL.
-- id: copy.col_priv_count
-- min-version: 11
-- requires: all_col_privs
-- license: none
SELECT COUNT(*) FROM all_col_privs WHERE table_schema = :OWN AND table_name = :TBL
