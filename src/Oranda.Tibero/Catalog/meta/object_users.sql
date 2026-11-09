-- 이 객체를 쓰고 있는 다른 세션 수. @OWNER@·@NAME@ = 이스케이프한 리터럴 글.
-- Oracle 과 달라진 점: Tibero 는 SYS_CONTEXT('USERENV','SID') 를 못 쓰므로 내 세션 SID 를 v$mystat 에서 읽는다.
-- id: meta.object_users
-- dbms: tibero
-- min-version: 6
-- requires: v$access, v$mystat
-- license: none
SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = '@OWNER@' AND object = '@NAME@' AND sid <> (SELECT m.sid FROM v$mystat m WHERE ROWNUM = 1)
