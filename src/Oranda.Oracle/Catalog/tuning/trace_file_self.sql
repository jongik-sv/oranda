-- 내 세션의 트레이스 파일 서버 경로(TN-07, TSK-06-04 트레이스 보기·10053). v$diag_info 는 이 세션 것만 보인다. 바인드 없음
-- id: tuning.trace_file_self
-- min-version: 11
-- requires: v$diag_info
-- license: none
SELECT value
  FROM v$diag_info
 WHERE name = 'Default Trace File'
