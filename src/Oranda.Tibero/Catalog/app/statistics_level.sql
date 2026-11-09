-- 실제 계획(행 수·CR Gets) 수집을 위해 이 세션에서 계획 통계를 켠다(Tibero). STATISTICS_LEVEL 은 Tibero 에서 TBR-7001(문법 오류)이고,
-- 같은 일을 하는 세션 매개변수가 GATHER_SQL_PLAN_STAT 이다(실측: 켠 뒤 DISPLAY_CURSOR 'ALLSTATS LAST' 에 CR Gets 가 찬다).
-- id: app.statistics_level
-- dbms: tibero
-- min-version: 6
-- license: none
ALTER SESSION SET GATHER_SQL_PLAN_STAT = Y
