-- 표 다시 만들기: 원본 표의 FK(부모가 같은 소유자). 열은 위치 순서. 줄 모양은 copy.recreate_referencing_fks 와 같고 마지막에 부모 표가 더 붙는다. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_constraints 에는 deferrable·deferred 열이 없어(-8026, TM0b 3.5·3.9) 그 두 자리는 NULL 이다(열 위치를 지키려고 남긴다).
-- all_cons_columns.position 은 0부터지만 양쪽을 같은 기준으로 맞물리게만 쓰고 내보내지 않는다.
-- id: copy.recreate_source_fks
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, CAST(NULL AS VARCHAR2(14)) AS deferrable, CAST(NULL AS VARCHAR2(9)) AS deferred, p.table_name FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position WHERE c.constraint_type = 'R' AND c.owner = :OWN AND c.table_name = :TBL AND p.owner = c.owner ORDER BY c.constraint_name, cc.position
