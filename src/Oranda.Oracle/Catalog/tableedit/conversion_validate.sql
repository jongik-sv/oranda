-- 열 형식 변환 검사(설계 18.9): 기본 변환식으로 바꿀 수 없는 행 수(Oracle 12.2+ VALIDATE_CONVERSION 집합 질의).
-- @WHERE@ = 앱이 만든 조건(사용자 원문 없음: 열 이름·형식·정해진 날짜 형식만). 다른 변환식은 tableedit.conversion_scan/conversion_rows.
-- id: tableedit.conversion_validate
-- min-version: 12
-- license: none
SELECT COUNT(*) FROM @TABLE@ t WHERE @WHERE@
