-- 출처: 사내 쿼리 서식 샘플 query-format-basic.sql 사본(2026-10-22, TSK-13-43). 서식 규칙 = 쿼리 서식 4장
-- 이 파일의 표·칼럼(TB_M47_*)은 서식을 보여 주려는 예시 이름이며 실재하지 않아도 된다.
-- 보여 주는 것: 기본 SELECT · 쉼표 조인 · 외부 조인 (+) · GROUP BY · ORDER BY · 바인드 변수
-- 지키는 것: 대문자, 절 키워드 맨 앞 열, 본문 7번째 열, 앞 쉼표, 표 별칭 A·B·C(AS 없음),
--           칼럼에 함수를 씌우지 않는 조건(날짜는 바인드 쪽에서 변환, 반열린 구간)

-- 1) 기본 SELECT: 항목은 한 줄에 하나(120자 안이면 한 줄에 여러 개도 가능)
SELECT A.PROC_CD
     , A.LINE_CD
     , A.PRD_NM_CD
     , A.COIL_WGT
FROM   TB_M47_PRD_ACT_CMN A
WHERE  A.PDN_PST_DD BETWEEN :fromDd AND :toDd
AND    A.PROC_CD IN ('1P', '33', '51')
ORDER BY A.PDN_PST_DD DESC, A.PROC_CD;

-- 2) 쉼표 조인: 표는 FROM 에 앞 쉼표로 나열, 조인 조건 먼저 → 필터 조건
--    조인 조건은 새로 붙는 표의 칼럼을 왼쪽에 쓴다
SELECT A.PROC_CD
     , B.PROC_NM
     , A.COIL_WGT
FROM   TB_M47_PRD_ACT_CMN A
     , TB_M47_PROC B
WHERE  B.PROC_CD = A.PROC_CD
AND    A.PDN_PST_DD BETWEEN :fromDd AND :toDd;

-- 3) 외부 조인 (+): C 쪽 표의 모든 조건에 (+) 를 붙인다(상수 비교 C.USE_YN(+) = 'Y' 도 포함)
SELECT A.PROC_CD
     , B.PROC_NM
     , C.LINE_NM
FROM   TB_M47_PRD_ACT_CMN A
     , TB_M47_PROC B
     , TB_M47_LINE C
WHERE  B.PROC_CD = A.PROC_CD
AND    C.LINE_CD(+) = A.LINE_CD
AND    C.USE_YN(+)  = 'Y'
AND    A.PDN_PST_DD BETWEEN :fromDd AND :toDd;

-- 4) GROUP BY · HAVING · ORDER BY: 항목이 길면 앞 쉼표로 줄을 나누고, 짧으면 한 줄 나열
SELECT A.PROC_CD
     , SUBSTR(A.PDN_PST_DD, 1, 6) YM
     , SUM(A.COIL_WGT) / 1000 WGT_T
     , COUNT(*) CNT
FROM   TB_M47_PRD_ACT_CMN A
WHERE  A.PDN_PST_DD BETWEEN :fromDd AND :toDd
GROUP BY A.PROC_CD
     , SUBSTR(A.PDN_PST_DD, 1, 6)
HAVING SUM(A.COIL_WGT) > 0
ORDER BY A.PROC_CD, YM;

-- 5) 인덱스를 살리는 날짜 조건: TIMESTAMP 칼럼은 원형 그대로 두고 반열린 구간으로 비교한다
SELECT A.PROC_CD
     , A.ACT_STR_DTM
     , TO_CHAR(A.ACT_STR_DTM, 'YYYY-MM-DD HH24:MI') ACT_STR_TXT   -- 표시용 변환은 SELECT 목록에서만
FROM   TB_M47_PRD_ACT_LOG A
WHERE  A.ACT_STR_DTM >= TO_DATE(:fromDd, 'YYYYMMDD')
AND    A.ACT_STR_DTM <  TO_DATE(:toDd, 'YYYYMMDD') + 1
AND    A.LOT_NO = :lotNo
ORDER BY A.ACT_STR_DTM DESC
FETCH FIRST 100 ROWS ONLY;
