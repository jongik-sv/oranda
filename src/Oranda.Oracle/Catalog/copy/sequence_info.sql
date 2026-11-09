-- 복사 사전 점검: 시퀀스 다음 값과 증가 폭. 다음 값(LAST_NUMBER)은 캐시가 있으면 실제보다 크거나 같다. 열 = LAST_NUMBER, INCREMENT_BY.
-- 바인드: OWN(시퀀스 소유자), SEQ(시퀀스 이름).
-- id: copy.sequence_info
-- min-version: 11
-- requires: all_sequences
-- license: none
SELECT LAST_NUMBER, INCREMENT_BY FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ
