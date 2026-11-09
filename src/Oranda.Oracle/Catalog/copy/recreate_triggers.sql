-- 표 다시 만들기: 대상 표에 걸린 트리거 목록. 열 = OWNER, TRIGGER_NAME, STATUS. 바인드: OWN, TBL.
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.recreate_triggers
-- min-version: 11
-- requires: all_triggers
-- license: none
SELECT owner, trigger_name, status FROM all_triggers WHERE table_owner = :OWN AND table_name = :TBL AND base_object_type = 'TABLE' ORDER BY trigger_name
