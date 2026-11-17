-- 열 형식 변환 검사(설계 18.9): 변환한 값(@EXPRESSION@ AS @COL@, 옛 열 이름)으로 CHECK 조건을 어기는 행 수 — 5단계가 제약을 다시 만들 때 실패하지 않을지.
-- @COLUMNS@ = 다른 열(`t."A", t."B", ` 꼴, 없으면 빈 글). 조건이 UNKNOWN 인 행은 어기지 않음. 사용자 원문 자리는 앞뒤 줄을 바꿔 둔다.
-- id: tableedit.conversion_check_violations
-- min-version: 11
-- license: none
SELECT COUNT(*) FROM (SELECT @COLUMNS@(
@EXPRESSION@
) AS @COL@ FROM @TABLE@ t) t WHERE NOT (
@CONDITION@
)
