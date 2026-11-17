-- 테이블 편집 검사: 열이 NULL 이 아닌 행 수(데이터 있는 열의 형식 바꾸기 ORA-01439, 숫자 줄이기 ORA-01440).
-- id: tableedit.nonnull_count
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@ WHERE @COL@ IS NOT NULL
