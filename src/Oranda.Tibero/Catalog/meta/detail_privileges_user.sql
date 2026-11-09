-- 객체 권한(접속 사용자가 볼 수 있는 것). 바인드: owner, name. dba 권한이 없을 때 쓴다.
-- Oracle 과 달리 Tibero 의 all_tab_privs 에는 table_schema 가 없고 소유자 열이 owner 다.
-- id: meta.detail_privileges_user
-- dbms: tibero
-- min-version: 6
-- requires: all_tab_privs
-- license: none
SELECT p.grantee, p.privilege, p.grantable, p.grantor
  FROM all_tab_privs p
 WHERE p.owner = :owner
   AND p.table_name = :name
 ORDER BY p.grantee, p.privilege
