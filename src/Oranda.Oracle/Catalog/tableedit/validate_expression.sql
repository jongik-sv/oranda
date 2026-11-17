-- 새 테이블의 식 문법 확인(설계 7.6): 빈 행(@SOURCE@ = CAST(NULL AS 형식) AS 열, …) 위에서 식을 해석만 한다(행 없음).
-- 사용자 원문 자리는 앞뒤에 줄을 바꿔 둔다: 원문 끝의 `-- 주석` 이 뒤 문장을 주석으로 만들지 않게.
-- id: tableedit.validate_expression
-- min-version: 11
-- license: none
SELECT (
@EXPRESSION@
) FROM (SELECT @SOURCE@ FROM DUAL) WHERE 1 = 0
