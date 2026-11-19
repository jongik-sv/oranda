-- 스키마 브라우저 트리거 표식(시점·단위·이벤트·꺼짐). 바인드: owner. 모든 트리거가 시점 표식을 가져 거르지 않는다(스키마당 트리거 수는 적다).
-- 열(ObjectMarkRows.Trigger): trigger_name, trigger_type(BEFORE EACH ROW·AFTER STATEMENT·INSTEAD OF·COMPOUND·BEFORE EVENT …),
-- base_object_type(TABLE·VIEW·SCHEMA·DATABASE), triggering_event(이벤트 트리거의 DDL·DB 이벤트 가름), status(ENABLED/DISABLED).
-- 트리거 본문(LONG)은 읽지 않는다.
-- id: meta.marks_triggers
-- min-version: 11
-- requires: dba_triggers, all_triggers
-- license: none
SELECT t.trigger_name, t.trigger_type, t.base_object_type, t.triggering_event, t.status
  FROM @PFX@_triggers t
 WHERE t.owner = :owner
