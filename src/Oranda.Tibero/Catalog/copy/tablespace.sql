-- 복사 사전 점검: 옛 대상 표의 테이블스페이스(SYSTEM 이면 휴지통에 남지 않는다). 열 = TABLESPACE_NAME. 바인드: OWN, TBL. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.tablespace
-- dbms: tibero
-- min-version: 6
-- requires: all_tables
-- license: none
SELECT tablespace_name FROM all_tables WHERE owner = :OWN AND table_name = :TBL
