-- 테이블 편집 실행 전 검사(설계 10장, TE-33): 행 수. @TABLE@ = 따옴표 붙인 소유자.테이블.
-- id: tableedit.count_rows
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@
