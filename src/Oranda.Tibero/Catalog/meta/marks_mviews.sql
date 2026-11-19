-- 스키마 브라우저 구체화 뷰 표식(갱신 방식·커밋 때 갱신·STALE). 바인드: owner. 열 순서·뜻은 Oracle 판과 같다(ObjectMarkRows.MView).
-- Tibero *_mviews 에도 refresh_method·refresh_mode·staleness 가 있다(6.2.15 실측, 시험 서버에는 구체화 뷰가 없어 값은 확인하지 못함).
-- id: meta.marks_mviews
-- dbms: tibero
-- min-version: 6
-- requires: dba_mviews, all_mviews
-- license: none
SELECT m.mview_name, m.refresh_method, m.refresh_mode, m.staleness
  FROM @PFX@_mviews m
 WHERE m.owner = :owner
