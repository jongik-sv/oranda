-- 잠긴 행 한 줄을 ROWID 로 읽는 문장. {table} = 따옴표로 감싼 OWNER.TABLE, {rowid} = 따옴표 안 ROWID 글.
-- id: app.locked_row_select
-- min-version: 11
-- license: none
SELECT * FROM {table} WHERE ROWID = '{rowid}'
