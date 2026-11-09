-- 복사 사전 점검: 표에 걸린 FGA 정책 수. 열 = 건수 하나. 바인드: OWN, TBL.
-- id: copy.fga_policy_count
-- min-version: 11
-- requires: all_audit_policies
-- license: none
SELECT COUNT(*) FROM all_audit_policies WHERE object_schema = :OWN AND object_name = :TBL
