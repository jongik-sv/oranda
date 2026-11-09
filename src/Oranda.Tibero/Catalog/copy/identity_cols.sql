-- 복사 사전 점검: IDENTITY 열과 생성 방식. 열 = COLUMN_NAME, GENERATION_TYPE. 바인드: OWN, TBL.
-- Tibero 에는 IDENTITY 열이 없다: GENERATED ... AS IDENTITY 문법이 -7001, all_tab_identity_cols 가 -8103, all_tab_columns 에 identity_column 이 없다
-- (TM0b 실측, 대체는 DEFAULT 시퀀스.NEXTVAL). 그래서 사전을 읽지 않고 같은 열의 빈 결과를 돌려준다.
-- 바인드는 글에 남겨 두어 호출 쪽이 보내는 두 이름이 모두 쓰이게 한다.
-- id: copy.identity_cols
-- dbms: tibero
-- min-version: 6
-- requires: dual
-- license: none
SELECT CAST(NULL AS VARCHAR2(128)) AS COLUMN_NAME, CAST(NULL AS VARCHAR2(10)) AS GENERATION_TYPE FROM DUAL WHERE 1 = 0 AND :OWN IS NOT NULL AND :TBL IS NOT NULL
