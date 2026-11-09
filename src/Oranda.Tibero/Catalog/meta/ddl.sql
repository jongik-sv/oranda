-- DBMS_METADATA DDL(Tibero). 바인드: p_type(DBMS_METADATA 형식 이름), name, owner, terminator, storage, segment_attributes, tablespace, dependents(0|1), ddl(OUT CLOB).
-- Oracle 과 달라진 점
--  - 바인드 이름 type 이 p_type 이다: Tibero PSM 블록에서 :type 은 예약어라 파싱 오류(ODP-15002)가 난다.
--  - GET_DDL 은 TABLE 만 받는다(실측). SEQUENCE·TRIGGER·VIEW·PROCEDURE·FUNCTION·SYNONYM 등은 ODP-14001(Not yet implemented), INDEX·PACKAGE·TYPE 은 빈 결과,
--    없는 TABLE 은 ODP-15104(No matching data found)가 그대로 올라간다. 그 외 형식은 읽는 쪽이 사전 뷰(*_source, *_views, *_indexes, *_triggers 등)로 따로 만든다.
--  - Tibero 의 DBMS_METADATA 에는 SET_TRANSFORM_PARAM·SESSION_TRANSFORM·GET_DEPENDENT_DDL 이 없다.
--    바인드 terminator 는 결과 끝의 ';' 를 붙이거나 떼서 맞춘다. storage·segment_attributes·tablespace·dependents 는 적용할 수 없어 바인드만 받고 무시한다
--    (서버가 만든 속성이 그대로 들어가고, 인덱스·제약·주석·권한·트리거 DDL 은 붙지 않는다).
-- id: meta.ddl
-- dbms: tibero
-- min-version: 6
-- requires: dbms_metadata
-- license: none
DECLARE
  l_ddl CLOB;
  l_len NUMBER;
  l_tail VARCHAR2(32);
  l_trim VARCHAR2(32);
  l_ws NUMBER;
  l_ignored NUMBER := NVL(:storage, 0) + NVL(:segment_attributes, 0) + NVL(:tablespace, 0) + NVL(:dependents, 0);
BEGIN
  l_ddl := DBMS_METADATA.GET_DDL(:p_type, :name, :owner);
  l_len := NVL(DBMS_LOB.GETLENGTH(l_ddl), 0);
  IF l_len > 0 THEN
    -- 결과 끝의 공백·줄바꿈을 빼고 마지막 글자가 ';' 인지 본다.
    l_tail := DBMS_LOB.SUBSTR(l_ddl, 32, GREATEST(l_len - 31, 1));
    l_trim := RTRIM(l_tail, ' ' || CHR(9) || CHR(10) || CHR(13));
    l_ws := LENGTH(l_tail) - NVL(LENGTH(l_trim), 0);
    IF SUBSTR(l_trim, -1) = ';' AND :terminator = 0 THEN
      DBMS_LOB.TRIM(l_ddl, l_len - l_ws - 1);
    ELSIF SUBSTR(l_trim, -1) <> ';' AND :terminator = 1 THEN
      DBMS_LOB.TRIM(l_ddl, l_len - l_ws);
      l_ddl := l_ddl || ';';
    END IF;
  END IF;
  :ddl := l_ddl;
END;
