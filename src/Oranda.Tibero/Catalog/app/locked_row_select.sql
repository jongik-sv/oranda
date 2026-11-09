-- 잠긴 행 한 줄을 ROWID 로 읽는 문장(Tibero, Oracle 과 같은 본문 — 실측: ROWIDTOCHAR 로 받은 글을 따옴표로 비교해 읽힘).
-- @TABLE@ = 따옴표로 감싼 OWNER.TABLE, @ROWID@ = 따옴표 안 ROWID 글. ROWID 를 만드는 lock.locked_row 는 Tibero 판이 아직 없다(docs/design/tibero-monitor-catalog.md 2장).
-- id: app.locked_row_select
-- dbms: tibero
-- min-version: 6
-- license: none
SELECT * FROM @TABLE@ WHERE ROWID = '@ROWID@'
