-- 복사 사전 점검: 표에 걸린 VPD 정책 수. 열 = 건수 하나. 바인드: OWN, TBL. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.vpd_policy_count
-- dbms: tibero
-- min-version: 6
-- requires: all_policies
-- license: none
SELECT COUNT(*) FROM all_policies WHERE object_owner = :OWN AND object_name = :TBL
