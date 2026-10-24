-- ASH 대기 분해(TN-14): sql_id 의 표본을 대기 이벤트로 묶는다(CPU 는 'ON CPU'). Diagnostics Pack 필요 — 보유 선언이 없으면 보내지 않는다.
-- 바인드: sql_id, since
-- id: tuning.ash_waits
-- min-version: 11
-- requires: gv$active_session_history
-- license: diagnostics
-- rac: gv
SELECT CASE WHEN a.session_state = 'ON CPU' THEN 'ON CPU' ELSE a.event END AS event,
       CASE WHEN a.session_state = 'ON CPU' THEN NULL ELSE a.wait_class END AS wait_class,
       COUNT(*) AS samples
  FROM gv$active_session_history a
 WHERE a.sql_id = :sql_id
   AND a.sample_time >= :since
 GROUP BY CASE WHEN a.session_state = 'ON CPU' THEN 'ON CPU' ELSE a.event END,
          CASE WHEN a.session_state = 'ON CPU' THEN NULL ELSE a.wait_class END
 ORDER BY samples DESC, event
