-- 바인드 캡처(v$sql_bind_capture). 서버가 캡처한 값만 있다(최초 실행·주기 갱신). 바인드: inst_id(null=전체), sql_id, child(null=전체 자식)
-- id: sql.binds
-- min-version: 19
-- requires: gv$sql_bind_capture
-- license: none
-- rac: gv
SELECT b.name, b.position, b.datatype_string, b.value_string, b.last_captured, b.was_captured
  FROM gv$sql_bind_capture b
 WHERE b.sql_id = :sql_id
   AND (:inst_id IS NULL OR b.inst_id = :inst_id)
   AND (:child IS NULL OR b.child_number = :child)
 ORDER BY b.child_number, b.position
