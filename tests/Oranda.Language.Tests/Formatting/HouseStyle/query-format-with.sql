-- 출처: 사내 쿼리 서식 샘플 query-format-with.sql 사본(2026-10-22, TSK-13-43). 서식 규칙 = 쿼리 서식 4장
-- 이 파일의 표·칼럼(TB_M47_*)은 서식을 보여 주려는 예시 이름이며 실재하지 않아도 된다.
-- 보여 주는 것: WITH(여러 개) · 인라인 뷰 · EXISTS / IN 서브쿼리 · CASE · 스칼라 서브쿼리
-- 규칙: 여러 줄 묶음은 여는 괄호·닫는 괄호가 각각 혼자 한 줄, 안쪽은 괄호 열 + 4칸.
--       안쪽에서도 키워드 맞춤·앞 쉼표 규칙은 그대로이고 기준 열만 옮겨 간다.

-- 1) WITH 여러 개: WITH 이름 AS 다음 줄에 ( 혼자, 다음 CTE 는 맨 앞 열에서 ", 이름 AS"
WITH T_ACT AS
(
    SELECT A.PROC_CD
         , SUM(A.COIL_WGT) WGT
    FROM   TB_M47_PRD_ACT_CMN A
    WHERE  A.PDN_PST_DD >= :fromDd
    GROUP BY A.PROC_CD
)
, T_PROC AS
(
    SELECT B.PROC_CD
         , B.PROC_NM
    FROM   TB_M47_PROC B
)
SELECT X.PROC_CD
     , Y.PROC_NM
     , X.WGT
FROM   T_ACT X
     , T_PROC Y
WHERE  Y.PROC_CD(+) = X.PROC_CD;

-- 2) 인라인 뷰(앞 쉼표 항목, 여는 괄호는 쉼표와 같은 줄) + EXISTS 서브쿼리(키워드 줄 뒤 다음 줄에 괄호)
SELECT A.PROC_CD
     , V.WGT
FROM   TB_M47_PROC A
     , (
           SELECT B.PROC_CD
                , SUM(B.COIL_WGT) WGT
           FROM   TB_M47_PRD_ACT_CMN B
           GROUP BY B.PROC_CD
       ) V
WHERE  V.PROC_CD(+) = A.PROC_CD
AND    EXISTS
       (
           SELECT 1
           FROM   TB_M47_LINE C
           WHERE  C.PROC_CD = A.PROC_CD
       );

-- 3) 인라인 뷰가 FROM 첫 항목이면 FROM 과 같은 줄 7번째 열에 ( 를 쓴다
SELECT V.PROC_CD
     , V.WGT
     , A.PROC_NM
FROM   (
           SELECT B.PROC_CD
                , SUM(B.COIL_WGT) WGT
           FROM   TB_M47_PRD_ACT_CMN B
           WHERE  B.PDN_PST_DD BETWEEN :fromDd AND :toDd
           GROUP BY B.PROC_CD
       ) V
     , TB_M47_PROC A
WHERE  A.PROC_CD = V.PROC_CD;

-- 4) IN 서브쿼리: 키워드 줄(AND    A.X IN)은 괄호 앞에서 끝낸다
SELECT A.ITEM_CD
     , A.ITEM_NM
FROM   TB_M47_ITEM A
WHERE  A.USE_YN = 'Y'
AND    A.ITEM_CD IN
       (
           SELECT B.ITEM_CD
           FROM   TB_M47_STOCK B
           WHERE  B.PLANT_CD = :plantCd
           AND    B.STOCK_QTY > 0
       );

-- 5) CASE: 짧으면 한 줄, 길면 WHEN·ELSE 를 CASE 열 + 5 칸에, END 는 CASE 와 같은 열(뒤에 별칭)
SELECT A.PROC_CD
     , CASE WHEN A.USE_YN = 'Y' THEN '사용' ELSE '미사용' END USE_NM
     , CASE WHEN A.COIL_WGT >= 20000 THEN 'H'
            WHEN A.COIL_WGT >= 10000 THEN 'M'
            ELSE 'L'
       END WGT_GRADE
FROM   TB_M47_PRD_ACT_CMN A
WHERE  A.PDN_PST_DD BETWEEN :fromDd AND :toDd;

-- 6) 짧은 서브쿼리: 들여쓰기 포함 그 줄이 120자 안이면 EXISTS·IN·스칼라 모두 한 줄로 써도 된다
SELECT A.PROC_CD
     , A.PROC_NM
     , (SELECT MAX(B.PDN_PST_DD) FROM TB_M47_PRD_ACT_CMN B WHERE B.PROC_CD = A.PROC_CD) LAST_DD
FROM   TB_M47_PROC A
WHERE  A.USE_YN = 'Y'
AND    EXISTS (SELECT 1 FROM TB_M47_LINE C WHERE C.PROC_CD = A.PROC_CD);
