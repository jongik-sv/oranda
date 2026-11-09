-- 복사 사전 점검: 대상 소유자에 그 이름의 객체가 있는가(다시 만들 표의 기본값·가상 열 식이 가리키는 시퀀스·함수). 열 = 건수 하나.
-- 바인드: OWN, NAME(객체 이름). (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.object_exists
-- dbms: tibero
-- min-version: 6
-- requires: all_objects
-- license: none
SELECT COUNT(*) FROM all_objects WHERE owner = :OWN AND object_name = :NAME
