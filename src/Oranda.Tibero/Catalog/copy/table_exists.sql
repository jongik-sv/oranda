-- 복사 실행: 정리 단계에서 대상 표가 아직 있는지. 열 = 건수 하나. 바인드: OWNER, NAME. (Oracle 판과 문장 동일)
-- id: copy.table_exists
-- dbms: tibero
-- min-version: 6
-- requires: all_tables
-- license: none
SELECT COUNT(*) FROM all_tables WHERE owner = :OWNER AND table_name = :NAME
