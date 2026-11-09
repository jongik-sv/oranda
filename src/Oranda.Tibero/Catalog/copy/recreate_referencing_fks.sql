-- 표 다시 만들기: 이 표의 PK·UK 를 참조하는 다른 표의 FK(자기 참조 제외). 열은 위치 순서.
-- 열 = 자식 소유자, 자식 표, 제약 이름, 삭제 규칙, 자식 열, 부모 열, 상태, DEFERRABLE, DEFERRED. 바인드: OWN, TBL.
-- Oracle 판과 달라진 점: Tibero 의 all_constraints 에는 deferrable·deferred 열이 없어(-8026, TM0b 3.5·3.9) 그 두 자리는 NULL 이다(열 위치를 지키려고 남긴다).
-- 지연 여부를 읽을 수 없으므로 다시 만든 FK 는 기본(NOT DEFERRABLE)이 된다. all_cons_columns.position 은 0부터지만 양쪽을 같은 기준으로 맞물리게만 쓰고 내보내지 않는다.
-- id: copy.recreate_referencing_fks
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, CAST(NULL AS VARCHAR2(14)) AS deferrable, CAST(NULL AS VARCHAR2(9)) AS deferred FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position WHERE c.constraint_type = 'R' AND p.owner = :OWN AND p.table_name = :TBL AND NOT (c.owner = p.owner AND c.table_name = p.table_name) ORDER BY c.owner, c.table_name, c.constraint_name, cc.position
