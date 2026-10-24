-- 이 sql_id 의 SQL Profile·Baseline·Patch(TN-15, 보기만). 공유 풀의 signature 로 찾는다(밀려난 sql_id 는 빈 결과).
-- Profile 은 force_matching 이면 force_matching_signature 로도 맞춘다. dba_ 뷰라 권한이 없으면 호출 쪽이 빈 목록으로 본다. 바인드: sql_id
-- id: tuning.plan_controls
-- min-version: 11
-- requires: gv$sql, dba_sql_profiles, dba_sql_plan_baselines, dba_sql_patches
-- license: none
-- rac: gv
WITH sig AS (
  SELECT DISTINCT q.exact_matching_signature AS exact_sig, q.force_matching_signature AS force_sig
    FROM gv$sql q
   WHERE q.sql_id = :sql_id
)
SELECT 'PROFILE' AS kind, p.name, p.status, CAST(p.created AS DATE) AS created, p.sql_text, CAST(NULL AS VARCHAR2(3)) AS accepted
  FROM dba_sql_profiles p
 WHERE EXISTS (SELECT 1 FROM sig
                WHERE p.signature = sig.exact_sig
                   OR (p.force_matching = 'YES' AND p.signature = sig.force_sig))
UNION ALL
SELECT 'BASELINE', b.plan_name, CASE b.enabled WHEN 'YES' THEN 'ENABLED' ELSE 'DISABLED' END, CAST(b.created AS DATE), b.sql_text, b.accepted
  FROM dba_sql_plan_baselines b
 WHERE EXISTS (SELECT 1 FROM sig WHERE b.signature = sig.exact_sig)
UNION ALL
SELECT 'PATCH', t.name, t.status, CAST(t.created AS DATE), t.sql_text, CAST(NULL AS VARCHAR2(3))
  FROM dba_sql_patches t
 WHERE EXISTS (SELECT 1 FROM sig WHERE t.signature = sig.exact_sig)
