-- 옵티마이저 트레이스(10053) 켜기: 이 세션에만 걸린다.
-- id: app.trace_event_on
-- min-version: 11
-- license: none
ALTER SESSION SET EVENTS '10053 trace name context forever, level 1'
