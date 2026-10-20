-- 테이블·뷰에 걸린 트리거(뷰는 INSTEAD OF). 바인드: owner, name. 본문(trigger_body)은 LONG 이라 읽지 않는다.
-- id: meta.detail_triggers
-- min-version: 19
-- requires: dba_triggers, all_triggers
-- license: none
SELECT t.trigger_name, t.trigger_type, t.triggering_event, t.status, t.when_clause, t.owner
  FROM @PFX@_triggers t
 WHERE t.table_owner = :owner
   AND t.table_name = :name
 ORDER BY t.trigger_name, t.owner
