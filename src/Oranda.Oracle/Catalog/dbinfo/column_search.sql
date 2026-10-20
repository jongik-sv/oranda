-- 열 이름 검색(테이블·뷰·머티리얼라이즈드 뷰). 바인드: owner, name_pattern(LIKE, 이스케이프 \, 대소문자 무시), max_rows
-- 객체 유형은 잘라 낸 줄에만 읽는다. 머티리얼라이즈드 뷰는 같은 이름의 TABLE 도 있어 MIN 으로 MATERIALIZED VIEW 를 고른다.
-- id: dbinfo.column_search
-- min-version: 19
-- requires: dba_tab_columns, all_tab_columns, dba_objects, all_objects
-- license: none
SELECT r.owner, r.table_name,
       (SELECT MIN(o.object_type)
          FROM @PFX@_objects o
         WHERE o.owner = r.owner
           AND o.object_name = r.table_name
           AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW', 'CLUSTER')) AS object_type,
       r.column_name, r.data_type, r.data_length, r.data_precision, r.data_scale, r.nullable
  FROM (SELECT c.owner, c.table_name, c.column_id, c.column_name, c.data_type,
               CASE WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.char_length
                    WHEN c.data_type = 'RAW' THEN c.data_length END AS data_length,
               c.data_precision, c.data_scale, c.nullable
          FROM @PFX@_tab_columns c
         WHERE (:owner IS NULL OR c.owner = :owner)
           AND UPPER(c.column_name) LIKE UPPER(:name_pattern) ESCAPE '\'
           AND c.table_name NOT LIKE 'BIN$%'
         ORDER BY c.owner, c.table_name, c.column_id
         FETCH FIRST :max_rows ROWS ONLY) r
 ORDER BY r.owner, r.table_name, r.column_id
