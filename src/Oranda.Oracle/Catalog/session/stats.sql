-- 세션 통계(0 이 아닌 것). 바인드: inst_id, sid
-- id: session.stats
-- min-version: 19
-- requires: gv$sesstat, gv$statname
-- license: none
-- rac: gv
SELECT n.name, ss.value
  FROM gv$sesstat ss
  JOIN gv$statname n ON n.inst_id = ss.inst_id AND n.statistic# = ss.statistic#
 WHERE ss.inst_id = :inst_id AND ss.sid = :sid AND ss.value <> 0
 ORDER BY n.name
