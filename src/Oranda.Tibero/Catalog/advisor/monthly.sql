-- AI 파티션 추천: 달마다 건수. @LABEL@ = 열에서 YYYY-MM 글을 만드는 식, @TABLE@ = 따옴표 친 이름, @SAMPLE@ = 빈 글 또는 " SAMPLE (p)". 달 글이 NULL 인 줄 = 값이 NULL 인 행. (Oracle advisor 와 같은 문장 — Tibero 실서버에서 그대로 돎)
-- id: advisor.monthly
-- dbms: tibero
-- min-version: 6
-- license: none
SELECT /* oranda-advisor */ @LABEL@ AS ym, COUNT(*) AS cnt
  FROM @TABLE@@SAMPLE@
 GROUP BY @LABEL@
