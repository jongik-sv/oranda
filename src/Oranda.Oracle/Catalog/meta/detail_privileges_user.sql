-- 객체 권한(접속 사용자가 볼 수 있는 것). 바인드: owner, name. dba 권한이 없을 때 쓴다(all_tab_privs 의 소유자 열은 table_schema).
-- id: meta.detail_privileges_user
-- min-version: 19
-- requires: all_tab_privs
-- license: none
SELECT p.grantee, p.privilege, p.grantable, p.grantor
  FROM all_tab_privs p
 WHERE p.table_schema = :owner
   AND p.table_name = :name
 ORDER BY p.grantee, p.privilege
