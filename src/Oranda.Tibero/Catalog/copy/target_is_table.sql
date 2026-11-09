-- 복사 사전 점검: 대상이 표인가(뷰·시노님·MV 는 대상으로 쓰지 않는다). 권한이 하나라도 있으면 보인다. 열 = 건수 하나. 바인드: OWN, TBL.
-- MV 의 저장 표는 all_tables 에도 같은 이름으로 나오므로 all_mviews 에 있으면 뺀다. (Oracle 판과 문장 동일, TM0b 실측 됨)
-- id: copy.target_is_table
-- dbms: tibero
-- min-version: 6
-- requires: all_tables, all_mviews
-- license: none
SELECT COUNT(*) FROM all_tables t WHERE t.owner = :OWN AND t.table_name = :TBL AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = t.owner AND m.mview_name = t.table_name)
