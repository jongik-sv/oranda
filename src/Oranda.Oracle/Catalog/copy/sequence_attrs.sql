-- 복사(없으면 만들기·다시 만들기): 원본 테이블에 연결된 시퀀스를 대상에 만들 때 읽는 원본 속성. 숫자는 TO_CHAR 로 낸다(MAXVALUE 28자리는 long 을 넘는다).
-- 열 = MIN_VALUE, MAX_VALUE, INCREMENT_BY, CYCLE_FLAG, ORDER_FLAG, CACHE_SIZE, LAST_NUMBER. 바인드: OWN(시퀀스 소유자), SEQ(시퀀스 이름).
-- id: copy.sequence_attrs
-- min-version: 11
-- requires: all_sequences
-- license: none
SELECT TO_CHAR(MIN_VALUE), TO_CHAR(MAX_VALUE), TO_CHAR(INCREMENT_BY), CYCLE_FLAG, ORDER_FLAG, TO_CHAR(CACHE_SIZE), TO_CHAR(LAST_NUMBER) FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :OWN AND SEQUENCE_NAME = :SEQ
