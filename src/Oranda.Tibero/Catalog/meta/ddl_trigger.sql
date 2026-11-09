-- 트리거 하나의 사용 상태(ENABLED·DISABLED). 바인드: owner, name. 트리거 DDL 끝의 ALTER TRIGGER … ENABLE|DISABLE 을 정한다(Oracle GET_DDL 모양).
-- 쓰는 열(OWNER·TRIGGER_NAME·STATUS)은 실측표의 *_TRIGGERS 열 목록에 있다.
-- id: meta.ddl_trigger
-- dbms: tibero
-- min-version: 6
-- requires: dba_triggers, all_triggers
-- license: none
SELECT t.status
  FROM @PFX@_triggers t
 WHERE t.owner = :owner
   AND t.trigger_name = :name
