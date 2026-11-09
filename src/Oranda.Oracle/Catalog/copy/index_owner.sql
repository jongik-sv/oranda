-- 복사 사전 점검: 대상 소유자의 그 이름 인덱스가 걸린 표. 열 = TABLE_NAME. 바인드: OWN, NAME(인덱스 이름).
-- id: copy.index_owner
-- min-version: 11
-- requires: all_indexes
-- license: none
SELECT table_name FROM all_indexes WHERE owner = :OWN AND index_name = :NAME
