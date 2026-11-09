-- EXPLAIN PLAN 텍스트(Tibero). DBMS_XPLAN.DISPLAY 가 없어 PLAN_TABLE 을 들여쓰기 표로 직접 그린다(열 이름은 Oracle 과 같은 plan_table_output).
-- 줄 = 「ID | 들여쓴 OPERATION | 객체 | Cost | Cards」, 그 아래 술어 줄(Access·Filter). format 은 받지만 쓰지 않는다(Tibero 에 형식 선택 없음).
-- 바인드: stmt_id, format
-- id: plan.explain_text
-- dbms: tibero
-- min-version: 6
-- requires: plan_table
-- license: none
SELECT line AS plan_table_output
  FROM (SELECT 0 AS id, 0 AS part,
               RPAD('ID', 5) || '| ' || RPAD('Operation', 50) || '| ' || RPAD('Name', 30) || '| ' || LPAD('Cost', 10) || ' | ' || LPAD('Cards', 10) AS line
          FROM dual
         WHERE :format IS NULL OR :format IS NOT NULL
        UNION ALL
        SELECT p.id, 1,
               LPAD(TO_CHAR(p.id), 4) || ' | ' || RPAD(LPAD(' ', NVL(p.depth, 0) * 2) || p.operation, 50) || '| '
               || RPAD(NVL(CASE WHEN p.object_name IS NOT NULL THEN NVL2(p.object_owner, p.object_owner || '.', '') || p.object_name END, ' '), 30)
               || '| ' || LPAD(NVL(TO_CHAR(p.cost), ' '), 10) || ' | ' || LPAD(NVL(TO_CHAR(p.cardinality), ' '), 10)
          FROM plan_table p
         WHERE p.statement_id = :stmt_id
        UNION ALL
        SELECT p.id, 2, '     Access: ' || p.access_predicates
          FROM plan_table p
         WHERE p.statement_id = :stmt_id AND p.access_predicates IS NOT NULL
        UNION ALL
        SELECT p.id, 3, '     Filter: ' || p.filter_predicates
          FROM plan_table p
         WHERE p.statement_id = :stmt_id AND p.filter_predicates IS NOT NULL)
 ORDER BY id, part
