-- 표의 외래 키 가운데 앞머리 인덱스가 없는 열(Tibero). 본문은 Oracle app.fk_index_check 와 같다(ALL_CONSTRAINTS·ALL_CONS_COLUMNS·ALL_IND_COLUMNS 열 같음, 실측).
-- @OWNER_FILTER@ = 소유자 조건 줄(없으면 빈 글), @TABLE@ = 따옴표로 감싼 표 이름 리터럴.
-- 위치 보정: Tibero *_CONS_COLUMNS.POSITION 은 0부터, *_IND_COLUMNS.COLUMN_POSITION 은 1부터(실측 6.2.15) → 비교는 cc.position + 1, 내보내는 position 도 +1.
-- id: app.fk_index_check
-- dbms: tibero
-- min-version: 6
-- requires: all_constraints, all_cons_columns, all_ind_columns
-- license: none
SELECT c.constraint_name, cc.column_name, cc.position + 1 AS position
  FROM all_constraints c
  JOIN all_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
 WHERE c.constraint_type = 'R'
@OWNER_FILTER@   AND c.table_name = @TABLE@
   AND NOT EXISTS (SELECT 1 FROM all_ind_columns ic
                    WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name
                      AND ic.column_name = cc.column_name AND ic.column_position = cc.position + 1)
 ORDER BY c.constraint_name, cc.position
