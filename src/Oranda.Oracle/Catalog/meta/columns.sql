-- 테이블·뷰의 열. 바인드: owner, name. data_default 는 LONG(InitialLONGFetchSize = -1 로 읽는다). domain_name 은 23 이상에서만 채운다(columns.23.sql).
-- id: meta.columns
-- min-version: 19
-- requires: dba_tab_cols, all_tab_cols, dba_col_comments, all_col_comments
-- license: none
SELECT c.column_name,
       COALESCE(c.column_id, c.internal_column_id) AS position,
       c.data_type,
       CASE WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.char_length
            WHEN c.data_type = 'RAW' THEN c.data_length END AS data_length,
       c.data_precision,
       c.data_scale,
       c.nullable,
       c.data_default,
       c.virtual_column,
       c.identity_column,
       c.hidden_column,
       CAST(NULL AS VARCHAR2(128)) AS domain_name,
       cm.comments
  FROM @PFX@_tab_cols c
  LEFT JOIN @PFX@_col_comments cm
    ON cm.owner = c.owner AND cm.table_name = c.table_name AND cm.column_name = c.column_name
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.user_generated = 'YES'
 ORDER BY c.internal_column_id
