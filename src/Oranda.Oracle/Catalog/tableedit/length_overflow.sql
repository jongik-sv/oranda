-- 테이블 편집 검사: 새 길이에 담기지 않는 행 수(ORA-01441). @LENGTH@ = LENGTH·LENGTHB·UTL_RAW.LENGTH, @COL@ = 따옴표 붙인 열. 바인드: max_length.
-- CHAR·NCHAR 는 빈칸 채움까지 센다(서버 판정과 같음, 실측).
-- id: tableedit.length_overflow
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@ WHERE @LENGTH@(@COL@) > :max_length
