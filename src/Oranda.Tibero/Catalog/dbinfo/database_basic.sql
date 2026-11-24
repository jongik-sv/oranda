-- 데이터베이스 정보 대신 질의. v$database 를 읽을 권한이 없는 계정용 — SYS_CONTEXT 와 database_properties(NAME, VALUE, COMMENT_STR)만 쓴다.
-- 실측(2026-11-10, CONNECT·RESOURCE 만 있는 계정): v$database·nls_database_parameters·global_name 은 ODP-8033, database_properties 는 읽힘
-- (NLS_CHARACTERSET·NLS_NCHAR_CHARACTERSET·DFLT_PERM_TS·DFLT_TEMP_TS·DB_NAME 행). 행이 없으면 NULL(MAX).
-- id: dbinfo.database_basic
-- dbms: tibero
-- min-version: 6
-- requires: database_properties
-- license: none
SELECT SYS_CONTEXT('USERENV', 'DB_NAME') AS name,
       MAX(CASE WHEN p.name = 'NLS_CHARACTERSET' THEN p.value END) AS characterset,
       MAX(CASE WHEN p.name = 'NLS_NCHAR_CHARACTERSET' THEN p.value END) AS nchar_characterset,
       MAX(CASE WHEN p.name = 'DFLT_PERM_TS' THEN p.value END) AS default_tablespace,
       MAX(CASE WHEN p.name = 'DFLT_TEMP_TS' THEN p.value END) AS temporary_tablespace
  FROM database_properties p
