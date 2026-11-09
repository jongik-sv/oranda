-- 복사 실행: 다시 만들기가 되살린 트리거의 컴파일 상태(VALID·INVALID). Tibero 는 없는 열을 가리키는 트리거 CREATE 를 오류 없이 받고 INVALID 로 남긴다(TB4 #79). 열 = 상태 하나. 바인드: OWN, NAME.
-- id: copy.trigger_status
-- dbms: tibero
-- min-version: 6
-- requires: all_objects
-- license: none
SELECT STATUS FROM ALL_OBJECTS WHERE OWNER = :OWN AND OBJECT_NAME = :NAME AND OBJECT_TYPE = 'TRIGGER'
