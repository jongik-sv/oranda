-- EXPLAIN PLAN 결과 단계(Tibero PLAN_TABLE). 열은 p.* 로 받아 이름으로 읽는다(Oracle plan.explain_steps 와 같은 계약).
-- Tibero PLAN_TABLE 열(실측): STATEMENT_ID, SQL_ID, CHILD_NUMBER, TIMESTAMP, OPERATION, OBJECT_OWNER, OBJECT_NAME, OBJECT_TYPE, SEARCH_COLUMNS,
-- ID, PARENT_ID, DEPTH, POSITION, COST, CARDINALITY, PARTITION_START, PARTITION_END, OTHERS, ACCESS_PREDICATES, FILTER_PREDICATES
-- (Oracle 과 달리 OPTIONS·BYTES·PLAN_ID 없음, OPERATION 이 「GROUP BY (SORT)」처럼 방식을 함께 담음). 바인드: stmt_id
-- id: plan.explain_steps
-- dbms: tibero
-- min-version: 6
-- requires: plan_table
-- license: none
SELECT p.*
  FROM plan_table p
 WHERE p.statement_id = :stmt_id
 ORDER BY p.id
