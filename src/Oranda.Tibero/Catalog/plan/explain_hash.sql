-- 예상 계획의 계획 해시(Tibero). PLAN_TABLE 에 계획 해시 열은 없지만 SQL_ID·CHILD_NUMBER 가 있고, 그 커서가 V$SQL 에 남아 PLAN_HASH_VALUE 를 준다(실측 M4, win144 #35:
-- 예상 계획 행의 sql_id·child_number → V$SQL 한 행, 같은 문장의 실제 커서 해시와 같은 값). 커서가 밀려났으면 행 없음 → 해시 null.
-- 바인드: stmt_id
-- id: plan.explain_hash
-- dbms: tibero
-- min-version: 6
-- requires: plan_table, v$sql
-- license: none
SELECT q.plan_hash_value
  FROM (SELECT p.sql_id, p.child_number
          FROM plan_table p
         WHERE p.statement_id = :stmt_id
           AND p.sql_id IS NOT NULL
           AND ROWNUM = 1) e
  JOIN v$sql q
    ON q.sql_id = e.sql_id
   AND q.child_number = e.child_number
 WHERE ROWNUM = 1
