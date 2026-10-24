-- 최근 데드락(LK-12). 알림 로그 v$diag_alert_ext(12.2 이후, 이 인스턴스)의 ORA-00060 최신순. 11.2 는 이 뷰가 없어 변형을 두지 않는다(빈 목록).
-- 시각은 UTC 로 바꿔 돌려준다. 바인드: max.
-- id: lock.deadlocks
-- min-version: 19
-- requires: v$diag_alert_ext
-- license: none
-- rac: v
SELECT at_utc, inst_id, message_text
  FROM (SELECT SYS_EXTRACT_UTC(a.originating_timestamp) AS at_utc, TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, a.message_text
          FROM v$diag_alert_ext a
         WHERE a.message_text LIKE '%ORA-00060%'
         ORDER BY a.originating_timestamp DESC)
 WHERE ROWNUM <= :max
