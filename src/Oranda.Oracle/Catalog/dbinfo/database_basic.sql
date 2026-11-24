-- 데이터베이스 정보 대신 질의. v$database 를 읽을 권한이 없는 계정용 — SYS_CONTEXT 와 PUBLIC 뷰(global_name·nls_database_parameters)만 쓴다.
-- id: dbinfo.database_basic
-- min-version: 11
-- requires: global_name, nls_database_parameters
-- license: none
SELECT SYS_CONTEXT('USERENV', 'DB_NAME') AS name,
       SYS_CONTEXT('USERENV', 'DB_DOMAIN') AS db_domain,
       (SELECT MAX(g.global_name) FROM global_name g) AS global_name,
       (SELECT MAX(n.value) FROM nls_database_parameters n WHERE n.parameter = 'NLS_CHARACTERSET') AS characterset,
       (SELECT MAX(n.value) FROM nls_database_parameters n WHERE n.parameter = 'NLS_NCHAR_CHARACTERSET') AS nchar_characterset
  FROM dual
