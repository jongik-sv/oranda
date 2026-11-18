-- AI 파티션 추천: 값마다 건수 상위 @TOP@ 개(NULL 은 맨 뒤) + 읽은 전체 행 수(tot)·NULL 행 수(nul). 빈 표 = 0줄. @COL@·@TABLE@ = 따옴표 친 이름, @SAMPLE@ = 빈 글 또는 " SAMPLE (p)". (Oracle advisor 와 같은 문장 — Tibero 실서버에서 그대로 돎)
-- id: advisor.top_values
-- dbms: tibero
-- min-version: 6
-- license: none
SELECT g.v, g.cnt, g.tot, g.nul
  FROM (SELECT s.v, s.cnt,
               SUM(s.cnt) OVER () AS tot,
               NVL(SUM(CASE WHEN s.v IS NULL THEN s.cnt END) OVER (), 0) AS nul,
               ROW_NUMBER() OVER (ORDER BY CASE WHEN s.v IS NULL THEN 1 ELSE 0 END, s.cnt DESC, s.v) AS rn
          FROM (SELECT /* oranda-advisor */ TO_CHAR(@COL@) AS v, COUNT(*) AS cnt
                  FROM @TABLE@@SAMPLE@
                 GROUP BY @COL@) s) g
 WHERE g.rn <= @TOP@
 ORDER BY g.rn
