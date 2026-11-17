-- 테이블 편집 검사: 키가 겹치는 행 수(PK·UK 추가, ORA-02299). 키 열이 모두 NULL 인 행은 뺀다(@ALL_NULL@).
-- 일부만 NULL 인 키는 GROUP BY 가 NULL 을 같은 값으로 묶는 것이 Oracle UK 판정과 같다(실측: (1,NULL) 두 줄은 ORA-02299).
-- id: tableedit.duplicate_keys
-- min-version: 11
-- license: none
SELECT NVL(SUM(cnt), 0)
  FROM (SELECT COUNT(*) AS cnt
          FROM @TABLE@
         WHERE NOT (@ALL_NULL@)
         GROUP BY @COLS@
        HAVING COUNT(*) > 1)
