-- 복사 사전 점검: 대상 표에 걸린 트리거 수(상태 무관). 「테이블 다시 만들기」는 꺼진 트리거도 되살리므로 CREATE TRIGGER 권한을 본다.
-- 열 = 건수 하나. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_triggers 에는 base_object_type 이 없어(-8026) 그 조건을 뺐다. 표에 걸린 트리거만 table_name 이 채워진다고 보며
-- (스키마·DB 트리거의 table_name 은 비어 있는 것으로 IT 가 확인한다) 같은 이름의 뷰 트리거는 표 이름과 겹치지 않는다.
-- id: copy.trigger_count_all
-- dbms: tibero
-- min-version: 6
-- requires: all_triggers
-- license: none
SELECT COUNT(*) FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL
