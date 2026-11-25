-- 이 객체를 지금 실행하고 있는 다른 세션 수, 접속한 인스턴스만(v$session). @OWNER@·@NAME@ = 이스케이프한 리터럴 글.
-- meta.object_users(gv$session)를 읽을 권한이 없고 V_$SESSION 만 받은 계정이 쓴다. RAC 의 다른 인스턴스 세션은 세지 못한다.
-- id: meta.object_users_local
-- min-version: 11
-- requires: v$session
-- license: none
-- rac: v
SELECT COUNT(DISTINCT s.sid) FROM v$session s WHERE (s.plsql_entry_object_id IN (SELECT o.object_id FROM all_objects o WHERE o.owner = '@OWNER@' AND o.object_name = '@NAME@') OR s.plsql_object_id IN (SELECT o.object_id FROM all_objects o WHERE o.owner = '@OWNER@' AND o.object_name = '@NAME@')) AND s.sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))