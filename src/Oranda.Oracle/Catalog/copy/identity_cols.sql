-- 복사 사전 점검: IDENTITY 열과 생성 방식. 열 = COLUMN_NAME, GENERATION_TYPE. 바인드: OWN, TBL.
-- 뷰는 12c 부터 있어 최소 버전 12(11.2 파싱 시험 Oracle11gTests 대상 아님). 11g 에는 IDENTITY 열이 없어 사전 점검이 이 조회를 보내지 않는다
-- (AdjustIdentityAsync 는 IsIdentity 열이 없으면 먼저 돌아간다).
-- id: copy.identity_cols
-- min-version: 12
-- requires: all_tab_identity_cols
-- license: none
SELECT COLUMN_NAME, GENERATION_TYPE FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = :OWN AND TABLE_NAME = :TBL
