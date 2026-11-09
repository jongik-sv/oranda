-- 복사 사전 점검: 대상 표에 걸린 트리거 수(상태 무관). 「테이블 다시 만들기」는 꺼진 트리거도 되살리므로 CREATE TRIGGER 권한을 본다.
-- 열 = 건수 하나. 바인드: OWN, TBL.
-- id: copy.trigger_count_all
-- min-version: 11
-- requires: all_triggers
-- license: none
SELECT COUNT(*) FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE'
