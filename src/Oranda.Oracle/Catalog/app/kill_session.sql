-- 세션 종료. @ID@ = 'sid,serial#[,@inst]' 안쪽 글.
-- id: app.kill_session
-- min-version: 11
-- license: none
ALTER SYSTEM KILL SESSION '@ID@' IMMEDIATE
