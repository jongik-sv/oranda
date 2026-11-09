-- 바인드 캡처(Tibero V$SQL_BIND_CAPTURE). 열은 Oracle sql.binds 와 같다: datatype_string = TYPE_NAME, value_string = VALUE,
-- last_captured = TIME_CAPTURED, was_captured = 캡처 시각이 있으면 YES(Tibero 에 그 열 없음). 바인드: inst_id(null=전체), sql_id, child(null=전체 자식)
-- id: sql.binds
-- dbms: tibero
-- min-version: 6
-- requires: v$sql_bind_capture
-- license: none
SELECT b.name, b.position, b.type_name AS datatype_string, b.value AS value_string, b.time_captured AS last_captured,
       CASE WHEN b.time_captured IS NOT NULL THEN 'YES' ELSE 'NO' END AS was_captured
  FROM v$sql_bind_capture b
 WHERE b.sql_id = :sql_id
   AND (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND (:child IS NULL OR b.child_number = :child)
 ORDER BY b.child_number, b.position
