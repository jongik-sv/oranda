-- 세션 연결 끊기. @ID@ = 'sid,serial#[,@inst]' 안쪽 글, @MODE@ = IMMEDIATE | POST_TRANSACTION.
-- id: app.disconnect_session
-- min-version: 11
-- license: none
ALTER SYSTEM DISCONNECT SESSION '@ID@' @MODE@
