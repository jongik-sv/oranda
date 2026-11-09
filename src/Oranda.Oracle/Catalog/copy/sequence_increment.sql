-- 복사 실행: 시퀀스 증가값·디스크에 적힌 상한(LAST_NUMBER)·캐시 크기. 시퀀스 SELECT 권한이 있으면 ALL_SEQUENCES 에 보인다. 열 = INCREMENT_BY, LAST_NUMBER, CACHE_SIZE.
-- 바인드: OWNER(시퀀스 소유자), NAME(시퀀스 이름).
-- id: copy.sequence_increment
-- min-version: 11
-- requires: all_sequences
-- license: none
SELECT increment_by, last_number, cache_size FROM all_sequences WHERE sequence_owner = :OWNER AND sequence_name = :NAME
