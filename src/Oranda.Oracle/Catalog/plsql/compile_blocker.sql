-- 컴파일(CREATE OR REPLACE)이 끝나지 않을 때 그 세션을 막는 세션(library cache pin/lock 대기). 바인드: sid(기다리는 세션).
-- 기다리는 세션이 아닌 다른 세션에서 읽는다(기다리는 세션은 명령이 끝나기 전에는 다른 문장을 보낼 수 없다).
-- 기다리는 세션이 있으면 한 행, 없으면 0행. 막는 세션을 서버가 모르면(blocking_session 이 비면) blocking_sid 이하가 비어 있다.
-- running_object = 막는 세션이 지금 실행 중인 PL/SQL 의 진입 객체(owner.name, 없으면 비어 있음).
-- 권한: v$session·dba_objects 를 읽을 수 있어야 한다(SELECT_CATALOG_ROLE 또는 SELECT ANY DICTIONARY). 없으면 ORA-00942.
-- 이 인스턴스만 본다(RAC 의 다른 인스턴스에서 막는 세션은 blocking_instance 로만 보인다).
-- id: plsql.compile_blocker
-- min-version: 11
-- requires: v$session, dba_objects
-- license: none
-- rac: v
SELECT w.sid AS waiting_sid,
       w.event AS waiting_event,
       w.seconds_in_wait AS waiting_seconds,
       w.blocking_session_status,
       w.blocking_instance,
       b.sid AS blocking_sid,
       b.serial# AS blocking_serial,
       b.username AS blocking_username,
       b.program AS blocking_program,
       b.machine AS blocking_machine,
       b.status AS blocking_status,
       b.event AS blocking_event,
       b.sql_id AS blocking_sql_id,
       CASE WHEN o.object_name IS NOT NULL THEN o.owner || '.' || o.object_name END AS running_object
  FROM v$session w
  LEFT JOIN v$session b ON b.sid = w.blocking_session
  LEFT JOIN dba_objects o ON o.object_id = b.plsql_entry_object_id
 WHERE w.sid = :sid
