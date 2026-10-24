-- AWR 계획 이력(TN-13): snap 구간별 plan_hash 와 실행 델타(인스턴스 합). Diagnostics Pack 필요 — 보유 선언이 없으면 보내지 않는다.
-- 바인드: sql_id
-- id: tuning.awr_plan_history
-- min-version: 11
-- requires: dba_hist_sqlstat, dba_hist_snapshot
-- license: diagnostics
SELECT st.snap_id, MAX(sn.end_interval_time) AS end_interval_time, st.plan_hash_value,
       SUM(st.executions_delta) AS executions, SUM(st.elapsed_time_delta) AS elapsed_time, SUM(st.buffer_gets_delta) AS buffer_gets
  FROM dba_hist_sqlstat st
  JOIN dba_hist_snapshot sn
    ON sn.snap_id = st.snap_id AND sn.dbid = st.dbid AND sn.instance_number = st.instance_number
 WHERE st.sql_id = :sql_id
 GROUP BY st.snap_id, st.plan_hash_value
 ORDER BY st.snap_id, st.plan_hash_value
