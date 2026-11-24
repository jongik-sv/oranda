-- 이 객체를 쓰고 있는 다른 세션 수. @OWNER@·@NAME@ = 이스케이프한 리터럴 글.
-- RAC 면 다른 인스턴스의 세션도 센다(gv$access, 내 세션 = 이 인스턴스의 내 SID). RAC 가 아니면 gv$ 는 v$ 와 같다.
-- id: meta.object_users
-- min-version: 11
-- requires: gv$access
-- license: none
-- rac: gv
SELECT COUNT(*) FROM (SELECT DISTINCT inst_id, sid FROM gv$access WHERE owner = '@OWNER@' AND object = '@NAME@' AND NOT (inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AND sid = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))))