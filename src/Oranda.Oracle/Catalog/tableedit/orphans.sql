-- 테이블 편집 검사: FK 를 더하면 짝이 없는 행 수(ORA-02298). 키 열이 하나라도 NULL 인 행은 뺀다(@ANY_NULL@). @MATCH@ = p.r1 = c.c1 AND ….
-- id: tableedit.orphans
-- min-version: 11
-- license: none
SELECT COUNT(*)
  FROM @TABLE@ c
 WHERE NOT (@ANY_NULL@)
   AND NOT EXISTS (SELECT 1 FROM @REF_TABLE@ p WHERE @MATCH@)
