-- 복사 사전 점검: 한 소유자의 시퀀스 이름 후보(SequenceGuess 입력). 열 = SEQUENCE_NAME. 바인드: OWN. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.sequence_names
-- dbms: tibero
-- min-version: 6
-- requires: all_sequences
-- license: none
SELECT SEQUENCE_NAME FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN ORDER BY SEQUENCE_NAME
