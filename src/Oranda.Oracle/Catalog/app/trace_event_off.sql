-- 옵티마이저 트레이스(10053) 끄기.
-- id: app.trace_event_off
-- min-version: 11
-- license: none
ALTER SESSION SET EVENTS '10053 trace name context off'
