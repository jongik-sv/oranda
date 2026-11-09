-- 세션 종료(Tibero). @ID@ = 'sid,serial#' 안쪽 글(Tibero 는 단일 인스턴스 기준이라 ',@inst' 를 붙이지 않는다).
-- Tibero 문법 = ALTER SYSTEM KILL SESSION (sid, serial#), IMMEDIATE 없음. 실측: 일반 계정은 TBR-17004(권한 없음)까지 파싱됨 — 실제 종료는 권한 계정 IT 로 확인.
-- id: app.kill_session
-- dbms: tibero
-- min-version: 6
-- license: none
ALTER SYSTEM KILL SESSION (@ID@)
