-- 테이블 편집 검사: 새 정밀도·소수(반올림 뒤)에 정수 자리가 담기지 않는 행 수. 바인드: scale, digits(= 정밀도 - 소수, 음수 가능).
-- 지금 정의보다 정밀도·소수를 줄이는 경우는 이 질의가 아니라 nonnull_count 다(데이터가 있으면 ORA-01440, 실측).
-- id: tableedit.number_overflow
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM @TABLE@ WHERE ABS(ROUND(@COL@, :scale)) >= POWER(10, :digits)
