-- 복사 사전 점검: 이 표를 참조하는 켜진 외래키(자기 참조 제외). 열 = OWNER, TABLE_NAME, CONSTRAINT_NAME. 바인드: OWN, TBL.
-- Oracle 판과 문장 동일(TM0b 실측 됨). 이 문장은 열 위치(POSITION)를 쓰지 않는다.
-- id: copy.referencing_fks
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints
-- license: none
SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'R' AND c.STATUS = 'ENABLED' AND p.OWNER = :OWN AND p.TABLE_NAME = :TBL AND NOT (c.OWNER = p.OWNER AND c.TABLE_NAME = p.TABLE_NAME) ORDER BY c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME
