-- 복사 사전 점검: 표에 걸린 켜진 트리거만. 열 = OWNER, TRIGGER_NAME. 바인드: OWN, TBL.
-- id: copy.enabled_triggers
-- min-version: 11
-- requires: all_triggers
-- license: none
SELECT OWNER, TRIGGER_NAME FROM ALL_TRIGGERS WHERE TABLE_OWNER = :OWN AND TABLE_NAME = :TBL AND STATUS = 'ENABLED' ORDER BY OWNER, TRIGGER_NAME
