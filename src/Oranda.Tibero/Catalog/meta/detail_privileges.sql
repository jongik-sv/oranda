-- 객체 권한(부여받은 사람). 바인드: owner, name. dba 권한이 있을 때 쓴다(dba_tab_privs 의 소유자 열은 owner). Oracle 판과 같은 문장
-- id: meta.detail_privileges
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_privs
-- license: none
SELECT p.grantee, p.privilege, p.grantable, p.grantor
  FROM dba_tab_privs p
 WHERE p.owner = :owner
   AND p.table_name = :name
 ORDER BY p.grantee, p.privilege
