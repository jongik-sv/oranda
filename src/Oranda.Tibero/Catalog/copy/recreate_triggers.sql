-- 표 다시 만들기: 대상 표에 걸린 트리거 목록. 열 = OWNER, TRIGGER_NAME, STATUS. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_triggers 에는 base_object_type 이 없어(-8026, TM0b 3.9) 그 조건을 뺐다. copy.trigger_count_all 과 같은 판정이다
-- (표에 걸린 트리거만 table_name 이 채워진다). 트리거 DDL 은 이 카탈로그가 아니라 메타데이터 제공자(ALL_SOURCE 조립)로 받는다(GET_DDL('TRIGGER') = -14001).
-- id: copy.recreate_triggers
-- dbms: tibero
-- min-version: 6
-- requires: all_triggers
-- license: none
SELECT owner, trigger_name, status FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL ORDER BY trigger_name
