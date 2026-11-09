-- 계획 이력(Tibero TPR: DBA_HIST_SQLSTAT·DBA_HIST_SNAPSHOT). 열은 Oracle tuning.awr_plan_history 와 같다.
-- Tibero 이력은 SQL_ID 가 아니라 SQL_HASH_VALUE 로 남는다 → sql_id(13자 base32)의 아래 32비트 = hash value(Oracle 과 같은 규칙, V$SQL 실측 일치)를 SQL 로 계산한다.
-- DBID·*_DELTA 열이 없다: executions·elapsed_time·buffer_gets 는 그 스냅숏 행의 값 그대로(구간 값인지 누적인지 docs/design/tibero-monitor-catalog.md 6장).
-- Tibero 는 관리 팩이 없어 license: none. 바인드: sql_id
-- id: tuning.awr_plan_history
-- dbms: tibero
-- min-version: 6
-- requires: dba_hist_sqlstat, dba_hist_snapshot
-- license: none
SELECT st.snap_id, MAX(sn.end_interval_time) AS end_interval_time, st.plan_hash_value,
       SUM(st.executions) AS executions, SUM(st.elapsed_time) AS elapsed_time, SUM(st.buffer_gets) AS buffer_gets
  FROM dba_hist_sqlstat st
  JOIN dba_hist_snapshot sn
    ON sn.snap_id = st.snap_id AND sn.instance_number = st.instance_number
 WHERE st.sql_hash_value = (SELECT MOD(SUM((INSTR('0123456789abcdfghjkmnpqrstuvwxyz', SUBSTR(:sql_id, LEVEL, 1)) - 1)
                                           * POWER(32, LENGTH(:sql_id) - LEVEL)), 4294967296)
                              FROM dual
                            CONNECT BY LEVEL <= LENGTH(:sql_id))
 GROUP BY st.snap_id, st.plan_hash_value
 ORDER BY st.snap_id, st.plan_hash_value
