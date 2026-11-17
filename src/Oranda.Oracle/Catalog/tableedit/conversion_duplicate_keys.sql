-- 열 형식 변환 검사(설계 18.9): 변환한 값으로 PK·UK 키(@KEYS@)가 겹치는 행 수(겹치는 묶음의 행을 모두, 모든 키 열이 NULL 인 행은 빼고 — UK 규칙).
-- @COLUMNS@ = 다른 열(`t."A", ` 꼴), @ALL_NULL@ = 키 열이 모두 NULL. 사용자 원문 자리는 앞뒤 줄을 바꿔 둔다.
-- id: tableedit.conversion_duplicate_keys
-- min-version: 11
-- license: none
SELECT NVL(SUM(n), 0) FROM (
  SELECT COUNT(*) AS n FROM (SELECT @COLUMNS@(
@EXPRESSION@
) AS @COL@ FROM @TABLE@ t) t
   WHERE NOT (@ALL_NULL@)
   GROUP BY @KEYS@
  HAVING COUNT(*) > 1)
