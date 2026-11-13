-- 백업 테이블 권유(되돌리기 지점 설계 9장): 문장에 스키마가 없을 때의 대상 소유자(현재 스키마)와 백업을 만들 스키마(세션 사용자).
-- Oracle 판과 같은 문장(RB-44). Tibero 6.2.15 실측: SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'·'SESSION_USER') 됨(RollbackDialectMeasureIt).
-- id: rollback.session_schema
-- dbms: tibero
-- min-version: 6
-- requires: dual
-- license: none
SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'), SYS_CONTEXT('USERENV', 'SESSION_USER') FROM DUAL
