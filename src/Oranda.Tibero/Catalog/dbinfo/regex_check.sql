-- 정규식 검사: 소스 검색 전에 서버가 패턴을 받아들이는지 본다. 바인드: pattern, flags('c' 또는 'i')
-- 거를 줄이 하나도 없으면 서버가 패턴을 평가하지 않아 오류를 놓치므로 dual 한 줄로 먼저 평가한다. Tibero 는 SELECT 목록 안의 REGEXP_LIKE 가 없어 WHERE 에서만 쓴다(Oracle 판과 같은 형태).
-- id: dbinfo.regex_check
-- dbms: tibero
-- min-version: 6
-- license: none
SELECT COUNT(*) AS n
  FROM dual
 WHERE REGEXP_LIKE('x', :pattern, :flags)
