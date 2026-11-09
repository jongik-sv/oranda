-- 초기화 파라미터(v$parameters).
-- Oracle 과 달라진 점: v$parameter 대신 v$parameters(NAME, VALUE, DFLT_VALUE)를 쓴다. isdefault 는 value 와 dflt_value 가 같은지(둘 다 NULL 이면 같음)로 만든 TRUE/FALSE.
-- ismodified·isses_modifiable·issys_modifiable·description 은 Tibero 뷰에 없어 NULL.
-- id: dbinfo.parameters
-- dbms: tibero
-- min-version: 6
-- requires: v$parameters
-- license: none
SELECT p.name, p.value,
       DECODE(p.value, p.dflt_value, 'TRUE', 'FALSE') AS isdefault,
       CAST(NULL AS VARCHAR2(9)) AS ismodified,
       CAST(NULL AS VARCHAR2(5)) AS isses_modifiable,
       CAST(NULL AS VARCHAR2(9)) AS issys_modifiable,
       CAST(NULL AS VARCHAR2(255)) AS description
  FROM v$parameters p
 ORDER BY p.name
