-- 자식 커서가 따로 생긴 사유(TN-04). 사유 열(…_MISMATCH 등)은 서버 버전마다 달라 전체 열을 읽고, 'Y' 인 열을 호출 쪽이 행으로 펼친다
-- (11.2 와 19c 이후의 열 차이를 한 파일로 받는다). reason 열(XML 상세)이 있으면 함께 읽는다. 바인드: sql_id
-- id: tuning.shared_cursor
-- min-version: 11
-- requires: gv$sql_shared_cursor
-- license: none
-- rac: gv
SELECT s.*
  FROM gv$sql_shared_cursor s
 WHERE s.sql_id = :sql_id
 ORDER BY s.inst_id, s.child_number
