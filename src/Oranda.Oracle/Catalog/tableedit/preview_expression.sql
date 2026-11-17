-- 가상 식 미리보기(설계 7.6): 식과 쓰인 열 몇 줄. @EXPRESSION@ = 식 원문, @COLUMNS@ = 앞에 쉼표를 붙인 열 목록(없으면 빈 글). 바인드: max_rows.
-- 사용자 원문 자리는 앞뒤에 줄을 바꿔 둔다: 원문 끝의 `-- 주석` 이 뒤 문장을 주석으로 만들지 않게.
-- id: tableedit.preview_expression
-- min-version: 11
-- license: none
SELECT (
@EXPRESSION@
) AS expression_value@COLUMNS@
  FROM @TABLE@
 WHERE ROWNUM <= :max_rows
