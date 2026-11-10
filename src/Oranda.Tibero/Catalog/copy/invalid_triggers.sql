-- 표 다시 만들기(TR1 위험 1): 대상 표에 걸린 트리거 가운데 지금 컴파일 오류(INVALID)인 것. 열 = OWNER, TRIGGER_NAME, STATUS(트리거 ENABLED·DISABLED). 바인드: OWN, TBL.
-- Tibero 에만 있는 id(Oracle 은 되살릴 때 ORA-24344 오류로 알아챈다). 컴파일 상태는 copy.trigger_status 와 같은 ALL_OBJECTS 의 STATUS 로 본다.
-- 받아 두기(TiberoRecreateStep.CaptureAsync)가 「지우기 전부터 INVALID」 를 기억하고, 사전 점검이 다시 만들기 확인 글에 이름을 적는다.
-- id: copy.invalid_triggers
-- dbms: tibero
-- min-version: 6
-- requires: all_triggers, all_objects
-- license: none
SELECT t.owner, t.trigger_name, t.status FROM all_triggers t JOIN all_objects o ON o.owner = t.owner AND o.object_name = t.trigger_name AND o.object_type = 'TRIGGER' WHERE t.table_owner = :OWN AND t.table_name = :TBL AND o.status = 'INVALID' ORDER BY t.owner, t.trigger_name
