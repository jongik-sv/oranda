-- 백업 테이블 권유(되돌리기 지점 설계 9장): DROP COLUMN 백업에 함께 담을 PK 열(위치 차례). 바인드: owner, name.
-- id: rollback.primary_key_columns
-- min-version: 11
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT cc.COLUMN_NAME FROM ALL_CONSTRAINTS c JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'P' AND c.OWNER = :owner AND c.TABLE_NAME = :name ORDER BY cc.POSITION
