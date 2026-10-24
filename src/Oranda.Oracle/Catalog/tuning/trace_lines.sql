-- 트레이스 본문(TN-07). v$diag_trace_file_contents 는 12.2 이후 — 11.2 에는 이 쿼리가 없고 기능을 이유와 함께 끈다.
-- 12.1 은 서비스가 버전(12.2 미만)을 보고 보내지 않는다. 바인드: file_name(경로를 뺀 파일 이름)
-- id: tuning.trace_lines
-- min-version: 12
-- requires: v$diag_trace_file_contents
-- license: none
SELECT c.payload
  FROM v$diag_trace_file_contents c
 WHERE c.trace_filename = :file_name
 ORDER BY c.line_number
