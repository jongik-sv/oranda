-- 복사 실행: 정리 단계에서 대상 표가 아직 있는지. 열 = 건수 하나. 바인드: OWNER, NAME.
-- id: copy.table_exists
-- min-version: 11
-- requires: all_tables
-- license: none
SELECT COUNT(*) FROM all_tables WHERE owner = :OWNER AND table_name = :NAME
