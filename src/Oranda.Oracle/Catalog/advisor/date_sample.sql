-- AI 파티션 추천: 날짜형 판정에 쓸 열 값 표본(글자로, 앞 500행). @TABLE@·@COL@ = 따옴표 친 이름, @SAMPLE@ = 빈 글 또는 " SAMPLE (p)". 형식 판정은 C# 몫.
-- id: advisor.date_sample
-- min-version: 11
-- license: none
SELECT /* oranda-advisor */ TO_CHAR(@COL@) AS v
  FROM @TABLE@@SAMPLE@
 WHERE @COL@ IS NOT NULL
   AND ROWNUM <= 500
