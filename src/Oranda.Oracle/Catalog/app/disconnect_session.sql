-- 세션 연결 끊기. {id} = 'sid,serial#[,@inst]' 안쪽 글, {mode} = IMMEDIATE | POST_TRANSACTION.
-- id: app.disconnect_session
-- min-version: 11
-- license: none
ALTER SYSTEM DISCONNECT SESSION '{id}' {mode}
