-- DBMS_METADATA DDL. 바인드: type(DBMS_METADATA 형식 이름), name, owner, terminator, storage, segment_attributes, tablespace, dependents(0|1), ddl(OUT CLOB).
-- id: meta.ddl
-- min-version: 19
-- requires: dbms_metadata
-- license: none
DECLARE
  l_ddl CLOB;
  l_type VARCHAR2(64) := :type;
  l_name VARCHAR2(128) := :name;
  l_owner VARCHAR2(128) := :owner;
  PROCEDURE add_dependent(p_type VARCHAR2) IS
  BEGIN
    l_ddl := l_ddl || CHR(10) || DBMS_METADATA.GET_DEPENDENT_DDL(p_type, l_name, l_owner);
  EXCEPTION
    WHEN OTHERS THEN
      IF SQLCODE NOT IN (-31608, -31603) THEN
        RAISE;
      END IF;
  END;
BEGIN
  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SQLTERMINATOR', :terminator = 1);
  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'STORAGE', :storage = 1);
  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'SEGMENT_ATTRIBUTES', :segment_attributes = 1);
  DBMS_METADATA.SET_TRANSFORM_PARAM(DBMS_METADATA.SESSION_TRANSFORM, 'TABLESPACE', :tablespace = 1);
  l_ddl := DBMS_METADATA.GET_DDL(l_type, l_name, l_owner);
  IF :dependents = 1 AND l_type = 'TABLE' THEN
    add_dependent('INDEX');
    add_dependent('CONSTRAINT');
    add_dependent('REF_CONSTRAINT');
    add_dependent('COMMENT');
    add_dependent('OBJECT_GRANT');
    add_dependent('TRIGGER');
  END IF;
  :ddl := l_ddl;
END;
