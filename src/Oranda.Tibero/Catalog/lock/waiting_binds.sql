-- 대기 세션 SQL 의 캡처된 바인드(Tibero V$SQL_BIND_CAPTURE). 열 순서·바인드는 Oracle lock.waiting_binds 와 같다.
-- datatype_string = TYPE_NAME, value_string = VALUE, last_captured = TIME_CAPTURED, was_captured = 캡처 시각이 있으면 YES(Tibero 에 그 열 없음).
-- 바인드 값은 형식을 못 박아(CAST) UNION ALL 의 두 갈래 형식을 맞춘다(없으면 TBR-11022).
-- sql_id 가 있으면 그 커서(child 가 null 이면 전체), 없으면 세션(sid·serial)의 현재 SQL(없으면 직전 SQL)과 자식 번호. 바인드: sql_id, child, inst_id, sid, serial.
-- id: lock.waiting_binds
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_bind_capture, v$session
-- license: none
SELECT b.name, b.position, b.type_name AS datatype_string, b.value AS value_string, b.time_captured AS last_captured,
       CASE WHEN b.time_captured IS NOT NULL THEN 'YES' ELSE 'NO' END AS was_captured
  FROM v$sql_bind_capture b
  JOIN (SELECT CAST(:sql_id AS VARCHAR2(64)) AS sql_id, CAST(:child AS NUMBER) AS child_number
          FROM dual
         WHERE :sql_id IS NOT NULL
        UNION ALL
        SELECT NVL(s.sql_id, s.prev_sql_id),
               CASE WHEN s.sql_id IS NOT NULL THEN s.sql_child_number ELSE s.prev_child_number END
          FROM v$session s
         WHERE :sql_id IS NULL
           AND (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
           AND s.sid = :sid AND s.serial# = :serial) t
    ON b.sql_id = t.sql_id
   AND (t.child_number IS NULL OR b.child_number = t.child_number)
 ORDER BY b.child_number, b.position
