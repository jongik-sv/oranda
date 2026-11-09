-- 복사 사전 점검: 대상 소유자의 그 이름 제약이 걸린 표. 열 = TABLE_NAME. 바인드: OWN, NAME(제약 이름). (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.constraint_owner
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints
-- license: none
SELECT table_name FROM all_constraints WHERE owner = :OWN AND constraint_name = :NAME
