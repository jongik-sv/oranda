-- dba_ddl_locks 를 읽을 권한 부여문. @USER@ = 받을 계정.
-- id: app.ddl_locks_grant
-- min-version: 11
-- license: none
GRANT SELECT ON sys.dba_ddl_locks TO @USER@;
