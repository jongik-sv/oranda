-- 표를 마스터로 하는 MView log(스키마 브라우저 Snapshot Log 폴더). 바인드: owner, name
-- id: meta.mview_log
-- min-version: 11
-- requires: dba_mview_logs, all_mview_logs
-- license: none
SELECT l.log_owner, l.log_table
  FROM @PFX@_mview_logs l
 WHERE l.log_owner = :owner
   AND l.master = :name
 ORDER BY l.log_table
