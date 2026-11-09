-- 복사 사전 점검: 대상이 표인가(뷰·시노님·MV 는 대상으로 쓰지 않는다). 권한이 하나라도 있으면 보인다. 열 = 건수 하나. 바인드: OWN, TBL.
-- MV 의 저장 표는 ALL_TABLES 에도 같은 이름으로 나오므로 ALL_MVIEWS 에 있으면 뺀다(11.2·26ai 모두 있는 뷰).
-- 본문은 옮기기 전 C# 문자열과 글자까지 같게 한 줄로 둔다.
-- id: copy.target_is_table
-- min-version: 11
-- requires: all_tables, all_mviews
-- license: none
SELECT COUNT(*) FROM all_tables t WHERE t.owner = :OWN AND t.table_name = :TBL AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = t.owner AND m.mview_name = t.table_name)
