-- 테이블·뷰의 열. 바인드: owner, name. data_default 는 LONG.
-- Oracle(11g 변형)과 달라진 점: Tibero 의 *_tab_cols 는 hidden_column·virtual_column 이 'Y'/'N' 이라 Oracle 과 같은 'YES'/'NO' 로 바꿔 돌려준다.
-- identity_column 열이 없어 identity_flag 는 NULL, domain_name 도 NULL. 문자 형식 이름이 VARCHAR/NVARCHAR 이므로 길이 판정에 함께 넣었다.
-- column_id 는 0부터라 Oracle 처럼 1부터 되게 1을 더했다. 숫자가 아닌 열은 data_precision/data_scale 이 0 으로 오므로 NUMBER·FLOAT 만 값을 내고 나머지는 NULL.
-- 끝 열 byte_length = 문자 열(VARCHAR2·CHAR)의 data_length(바이트). data_length 열(글자 수)과 같으면 바이트 단위 열이다 — 다시 만들기가 큰 바이트 열을 BYTE 로 남기는 데 쓴다(TR1).
-- id: meta.columns
-- dbms: tibero
-- min-version: 6
-- requires: dba_tab_cols, all_tab_cols, dba_col_comments, all_col_comments
-- license: none
SELECT c.column_name,
       COALESCE(c.column_id, c.internal_column_id) + 1 AS position,
       c.data_type,
       CASE WHEN c.data_type IN ('VARCHAR', 'VARCHAR2', 'NVARCHAR', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.char_length
            WHEN c.data_type = 'RAW' THEN c.data_length END AS data_length,
       CASE WHEN c.data_type IN ('NUMBER', 'FLOAT') THEN c.data_precision END AS data_precision,
       CASE WHEN c.data_type IN ('NUMBER', 'FLOAT') THEN c.data_scale END AS data_scale,
       c.nullable,
       c.data_default,
       CASE WHEN c.virtual_column = 'Y' THEN 'YES' ELSE 'NO' END AS virtual_column,
       CAST(NULL AS VARCHAR2(3)) AS identity_flag,
       CASE WHEN c.hidden_column = 'Y' THEN 'YES' ELSE 'NO' END AS hidden_column,
       CAST(NULL AS VARCHAR2(128)) AS domain_name,
       cm.comments,
       CASE WHEN c.data_type IN ('VARCHAR', 'VARCHAR2', 'CHAR') THEN c.data_length END AS byte_length
  FROM @PFX@_tab_cols c
  LEFT JOIN @PFX@_col_comments cm
    ON cm.owner = c.owner AND cm.table_name = c.table_name AND cm.column_name = c.column_name
 WHERE c.owner = :owner
   AND c.table_name = :name
   AND c.hidden_column = 'N'
 ORDER BY c.internal_column_id
