-- 복사 실행: FK 의 자식 열·부모 열(위치 순서)과 부모 표. 부모 없는 자식 행 수를 셀 때 쓴다. 열 = 자식 열, 부모 열, 부모 소유자, 부모 표. 바인드: OWNER, NAME(제약 이름).
-- Oracle 판과 문장 동일. Tibero 의 all_cons_columns.position 은 0부터지만 이 문장은 위치를 내보내지 않고 양쪽 같은 기준으로 맞물리게만 쓴다.
-- id: copy.fk_columns
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT cc.column_name, pc.column_name, p.owner, p.table_name FROM all_constraints c JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position WHERE c.owner = :OWNER AND c.constraint_name = :NAME ORDER BY cc.position
