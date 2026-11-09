-- 복사 사전 점검: 이 표를 참조하는 켜진 외래키(자기 참조 제외). 열 = OWNER, TABLE_NAME, CONSTRAINT_NAME. 바인드: OWN, TBL.
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.referencing_fks
-- min-version: 11
-- requires: all_constraints
-- license: none
SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND p.OWNER = :OWN AND p.TABLE_NAME = :TBL AND NOT (c.OWNER = p.OWNER AND c.TABLE_NAME = p.TABLE_NAME) ORDER BY c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME
