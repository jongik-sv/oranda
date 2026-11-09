-- 뷰 DDL 의 질의 글(CREATE VIEW … AS 뒤). 바인드: owner, name. 열 목록은 meta.columns 로 따로 읽는다(Oracle 처럼 열 이름을 괄호로 단다).
-- 실측표(docs/design/tibero-catalog-map.md)에 *_VIEWS 의 열 목록이 없다. Oracle 과 같은 OWNER·VIEW_NAME·TEXT 로 보고 쓰며,
-- 쓰기 IT(TiberoDdlObjectsIt)와 읽기 IT 의 파싱 확인이 이 열들을 드러낸다. TEXT 는 Oracle 에서 LONG 이고 Tibero 드라이버는 LONG·CLOB 을 글로 준다.
-- id: meta.ddl_view
-- dbms: tibero
-- min-version: 6
-- requires: dba_views, all_views
-- license: none
SELECT v.text
  FROM @PFX@_views v
 WHERE v.owner = :owner
   AND v.view_name = :name
