-- 서버 버전 배너. Tibero 의 v$version 은 NAME·VALUE 행(PRODUCT_MAJOR, BUILD_NUMBER, …)이고 BANNER 열은 비어 있어, 머리 한 줄 요약 뒤에 NAME = VALUE 줄을 그대로 잇는다. 결과 열은 banner 하나.
-- id: dbinfo.version
-- dbms: tibero
-- min-version: 6
-- requires: v$version
-- license: none
SELECT b.banner
  FROM (SELECT 0 AS ord, 0 AS rn,
               'Tibero ' || MAX(CASE WHEN v.name = 'PRODUCT_MAJOR' THEN v.value END)
               || ' (build ' || MAX(CASE WHEN v.name = 'BUILD_NUMBER' THEN v.value END) || ')' AS banner
          FROM v$version v
        UNION ALL
        SELECT 1, ROWNUM, v.name || ' = ' || v.value
          FROM v$version v) b
 ORDER BY b.ord, b.rn
