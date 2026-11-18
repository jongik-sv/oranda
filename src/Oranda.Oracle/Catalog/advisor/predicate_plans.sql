-- AI 파티션 추천(설계 2026-11-18 5장): 표 또는 그 표의 인덱스를 읽은 문장의 조건 글. 실행 수 많은 순 앞 @MAX@ 문장.
-- 공유 풀(V$SQL_PLAN)의 access_predicates·filter_predicates 와 문장 실행 수(V$SQL.executions 를 sql_id 로 합침). 조건 글에서 열을 찾는 일은 C# 몫.
-- 이 도구가 보낸 문장(주석 oranda-advisor)은 뺀다. V$ 를 볼 권한이 없으면 ORA-00942 를 그대로 던진다. 바인드: owner, name
-- id: advisor.predicate_plans
-- min-version: 11
-- requires: v$sql_plan, v$sql, dba_indexes, all_indexes
-- license: none
SELECT r.sql_id, r.executions, r.access_predicates, r.filter_predicates
  FROM (SELECT p.sql_id, x.executions, p.access_predicates, p.filter_predicates,
               DENSE_RANK() OVER (ORDER BY x.executions DESC, p.sql_id) AS rk
          FROM v$sql_plan p
          JOIN (SELECT sql_id, SUM(executions) AS executions
                  FROM v$sql
                 WHERE sql_text NOT LIKE '%oranda-advisor%'
                 GROUP BY sql_id) x
            ON x.sql_id = p.sql_id
         WHERE p.object_owner = :owner
           AND (p.object_name = :name
                OR p.object_name IN (SELECT i.index_name
                                       FROM @PFX@_indexes i
                                      WHERE i.table_owner = :owner
                                        AND i.table_name = :name))
           AND (p.access_predicates IS NOT NULL OR p.filter_predicates IS NOT NULL)) r
 WHERE r.rk <= @MAX@
 ORDER BY r.rk
