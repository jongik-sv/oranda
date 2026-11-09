-- 표의 외래 키 가운데 앞머리 인덱스가 없는 열. @OWNER_FILTER@ = 소유자 조건 줄(없으면 빈 글), @TABLE@ = 따옴표로 감싼 표 이름 리터럴.
-- id: app.fk_index_check
-- min-version: 11
-- requires: all_constraints, all_cons_columns, all_ind_columns
-- license: none
SELECT c.constraint_name, cc.column_name, cc.position
  FROM all_constraints c
  JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
 WHERE c.constraint_type = 'R'
@OWNER_FILTER@   AND c.table_name = @TABLE@
   AND NOT EXISTS (SELECT 1 FROM all_ind_columns ic
                    WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name
                      AND ic.column_name = cc.column_name AND ic.column_position = cc.position)
 ORDER BY c.constraint_name, cc.position
