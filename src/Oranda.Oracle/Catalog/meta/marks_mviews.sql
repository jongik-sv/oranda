-- 스키마 브라우저 구체화 뷰 표식(갱신 방식·커밋 때 갱신·STALE). 바인드: owner. 모든 구체화 뷰가 갱신 방식 표식을 가져 거르지 않는다(수가 적다).
-- 열(ObjectMarkRows.MView): mview_name, refresh_method(FAST·COMPLETE·FORCE·NEVER), refresh_mode(DEMAND·COMMIT·NEVER), staleness(FRESH·STALE·NEEDS_COMPILE·UNUSABLE …).
-- id: meta.marks_mviews
-- min-version: 11
-- requires: dba_mviews, all_mviews
-- license: none
SELECT m.mview_name, m.refresh_method, m.refresh_mode, m.staleness
  FROM @PFX@_mviews m
 WHERE m.owner = :owner
