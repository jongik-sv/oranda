-- 테이블 편집 검사: 열이 NULL 인 행 수(NOT NULL 추가, ORA-02296).
-- id: tableedit.null_count
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@ WHERE @COL@ IS NULL
