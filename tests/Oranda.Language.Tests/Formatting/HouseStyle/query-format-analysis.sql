-- 출처: 사내 쿼리 서식 샘플 query-format-analysis.sql 사본(2026-10-22, TSK-13-43). 서식 규칙 = 쿼리 서식 4장
-- 이 파일의 표·칼럼(TB_M47_*)은 서식을 보여 주려는 예시 이름이며 실재하지 않아도 된다.
-- 분석·임시 조회용 SQL 이다. 한글 별칭은 분석용에만 쓰고, 공백·괄호 등 특수문자가 있으면 큰따옴표로 감싼다.
-- 앱이 읽는 SQL(화면·매퍼·위젯)은 영문 대문자 별칭을 쓴다(query-format-basic.sql 참고).

-- 1) 기준 예시(사용자 원문을 포매터 출력으로 맞춤: 원문은 「"년월", A.PRD_NM_CD 품명」 이 한 줄, SUBSTR(MTL_CD, 7,1)·(9,2) 쉼표 뒤 공백 없음)
SELECT A.PROC_CD
     , SUBSTR(A.PDN_PST_DD, 1, 6) "년월"
     , A.PRD_NM_CD 품명
     , SUBSTR(MTL_CD, 7, 1) SPANGLE
     , SUBSTR(MTL_CD, 9, 2) 후처리
     , SUM(COIL_WGT) / 1000 "중량(T)"
     , SUM(COIL_WTH * COIL_LTH) / 1000 "면적(M2)"
     , SUM(COIL_WK_TIM) "작업시간(분)"
FROM   TB_M47_PRD_ACT_CMN A
WHERE  A.PDN_PST_DD BETWEEN '20250101' AND '20250102'
AND    A.PROC_CD IN ('1P', '33', '51', '82', '83', '84', '85')
GROUP BY A.PROC_CD
     , SUBSTR(A.PDN_PST_DD, 1, 6)
     , A.PRD_NM_CD
     , SUBSTR(MTL_CD, 7, 1)
     , SUBSTR(MTL_CD, 9, 2)
ORDER BY A.PROC_CD, SUBSTR(A.PDN_PST_DD, 1, 6), A.PRD_NM_CD, SUBSTR(MTL_CD, 7, 1), SUBSTR(MTL_CD, 9, 2);

-- 2) 같은 서식으로 쉼표 조인·외부 조인을 쓴 분석용 SQL
SELECT A.PROC_CD 공정코드
     , B.PROC_NM 공정명
     , C.LINE_NM "라인명(외부조인)"
     , SUM(A.COIL_WGT) / 1000 "중량(T)"
FROM   TB_M47_PRD_ACT_CMN A
     , TB_M47_PROC B
     , TB_M47_LINE C
WHERE  B.PROC_CD = A.PROC_CD
AND    C.LINE_CD(+) = A.LINE_CD
AND    C.USE_YN(+)  = 'Y'
AND    A.PDN_PST_DD BETWEEN '20250101' AND '20250102'
GROUP BY A.PROC_CD
     , B.PROC_NM
     , C.LINE_NM
ORDER BY A.PROC_CD, C.LINE_NM;
