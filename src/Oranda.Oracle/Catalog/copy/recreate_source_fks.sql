-- 표 다시 만들기: 원본 표의 FK(부모가 같은 소유자). 열은 위치 순서. 줄 모양은 copy.recreate_referencing_fks 와 같고 마지막에 부모 표가 더 붙는다. 바인드: OWN, TBL.
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.recreate_source_fks
-- min-version: 11
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred, p.table_name FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position WHERE c.constraint_type = 'R' AND c.owner = :OWN AND c.table_name = :TBL AND p.owner = c.owner ORDER BY c.constraint_name, cc.position
