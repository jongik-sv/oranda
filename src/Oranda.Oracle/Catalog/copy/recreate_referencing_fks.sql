-- 표 다시 만들기: 이 표의 PK·UK 를 참조하는 다른 표의 FK(자기 참조 제외). 열은 위치 순서.
-- 열 = 자식 소유자, 자식 표, 제약 이름, 삭제 규칙, 자식 열, 부모 열, 상태, DEFERRABLE, DEFERRED. 바인드: OWN, TBL.
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.recreate_referencing_fks
-- min-version: 11
-- requires: all_constraints, all_cons_columns
-- license: none
SELECT c.owner, c.table_name, c.constraint_name, c.delete_rule, cc.column_name, pc.column_name, c.status, c.deferrable, c.deferred FROM all_constraints c JOIN all_constraints p ON p.owner = c.r_owner AND p.constraint_name = c.r_constraint_name JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name JOIN all_cons_columns pc ON pc.owner = p.owner AND pc.constraint_name = p.constraint_name AND pc.position = cc.position WHERE c.constraint_type = 'R' AND p.owner = :OWN AND p.table_name = :TBL AND NOT (c.owner = p.owner AND c.table_name = p.table_name) ORDER BY c.owner, c.table_name, c.constraint_name, cc.position
