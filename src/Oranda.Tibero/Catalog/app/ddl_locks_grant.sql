-- SYS.DBA_DDL_LOCKS 를 읽을 권한 부여문(Tibero). @USER@ = 받을 계정. 권한 부여(쓰기)라 시험 서버에서 실행하지 않았다(문법은 Oracle 과 같은 GRANT).
-- Oracle 판 끝의 「;」 는 뺐다(드라이버에 보내는 한 문장).
-- id: app.ddl_locks_grant
-- dbms: tibero
-- min-version: 6
-- license: none
GRANT SELECT ON sys.dba_ddl_locks TO @USER@
