-- 스키마 브라우저 트리거 표식(시점·단위·이벤트·꺼짐). 바인드: owner. 열 순서는 Oracle 판과 같다(ObjectMarkRows.Trigger). 모든 트리거가 시점 표식을 가져 거르지 않는다.
-- Tibero *_triggers 에는 base_object_type 이 없다(NULL). 대신 trigger_type 이 이벤트 트리거를 BEFORE DDL·AFTER EVENT 처럼 나타낸다(6.2.15 실측),
-- DDL 과 DB 이벤트(STARTUP·LOGON …)는 triggering_event 로 가른다.
-- id: meta.marks_triggers
-- dbms: tibero
-- min-version: 6
-- requires: dba_triggers, all_triggers
-- license: none
SELECT t.trigger_name, t.trigger_type, NULL AS base_object_type, t.triggering_event, t.status
  FROM @PFX@_triggers t
 WHERE t.owner = :owner
