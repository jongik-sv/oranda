-- 플래시백 조회(AS OF)가 거슬러 갈 수 있는 시간: undo_retention(초).
-- Oracle 과 달라진 점: v$parameter 가 없어 v$parameters 를 쓰고, Tibero 의 파라미터 이름은 대문자(UNDO_RETENTION)다.
-- id: dbinfo.flashback_env
-- dbms: tibero
-- min-version: 6
-- requires: v$parameters
-- license: none
SELECT p.value AS undo_retention
  FROM v$parameters p
 WHERE UPPER(p.name) = 'UNDO_RETENTION'
