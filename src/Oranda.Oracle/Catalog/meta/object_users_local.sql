-- 이 객체를 쓰고 있는 다른 세션 수, 접속한 인스턴스만(v$access). @OWNER@·@NAME@ = 이스케이프한 리터럴 글.
-- meta.object_users(gv$access)를 읽을 권한이 없고 V_$ACCESS 만 받은 계정이 쓴다. RAC 의 다른 인스턴스 세션은 세지 못한다.
-- id: meta.object_users_local
-- min-version: 11
-- requires: v$access
-- license: none
SELECT COUNT(DISTINCT sid) FROM v$access WHERE owner = '@OWNER@' AND object = '@NAME@' AND sid <> TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))