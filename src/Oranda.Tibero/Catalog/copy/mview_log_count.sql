-- 복사 사전 점검: 표에 걸린 MV 로그 수. 열 = 건수 하나. 바인드: OWN, TBL. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.mview_log_count
-- dbms: tibero
-- min-version: 6
-- requires: all_mview_logs
-- license: none
SELECT COUNT(*) FROM all_mview_logs WHERE log_owner = :OWN AND master = :TBL
