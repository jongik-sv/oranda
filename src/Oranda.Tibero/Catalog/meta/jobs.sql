-- 작업(JOB) 목록 — 스키마 브라우저 「스케줄러 잡」 폴더. 바인드: owner(NULL = 모두), name_pattern(NULL = 모두, LIKE ESCAPE '\').
-- 열은 meta.objects 와 같다(owner, object_name, object_type = 'JOB', status = 'VALID', last_ddl_time·created = NULL). 받는 쪽이 목록에 덧붙인다.
-- 2026-11-21 Tibero 6 실측: *_objects 에 JOB 유형이 없다. 작업은 두 가지다 —
--   스케줄러 잡: *_scheduler_jobs(Oracle 과 같은 64열, 이름 = job_name).
--   DBMS_JOB: *_jobs(job 번호·schema_user = 사용자 번호(all_users.user_id)·broken·failures·next_date·interval·what). 이름이 있으면 *_jobs_with_name.name,
--   없으면 job 번호 글(예 '10'). all_jobs 는 자기 스키마 것만 보인다(뷰가 owner_id = USERENV('SCHEMAID') 로 거른다), dba_jobs 는 모두.
-- 이름 붙은 DBMS_JOB 이 같은 소유자의 스케줄러 잡과 이름이 같으면 스케줄러 잡 한 줄만 낸다(같은 작업이 두 번 보이지 않게).
-- id: meta.jobs
-- dbms: tibero
-- min-version: 6
-- requires: dba_scheduler_jobs, all_scheduler_jobs, dba_jobs, all_jobs, dba_jobs_with_name, all_jobs_with_name
-- license: none
SELECT x.owner, x.object_name, 'JOB' AS object_type, 'VALID' AS status, CAST(NULL AS DATE) AS last_ddl_time, CAST(NULL AS DATE) AS created
  FROM (SELECT j.owner, j.job_name AS object_name
          FROM @PFX@_scheduler_jobs j
        UNION ALL
        SELECT u.username, NVL(n.name, TO_CHAR(d.job))
          FROM @PFX@_jobs d
          JOIN all_users u ON u.user_id = d.schema_user
          LEFT JOIN @PFX@_jobs_with_name n ON n.job = d.job
         WHERE n.name IS NULL
            OR NOT EXISTS (SELECT 1 FROM @PFX@_scheduler_jobs j WHERE j.owner = u.username AND j.job_name = n.name)) x
 WHERE (:owner IS NULL OR x.owner = :owner)
   AND (:name_pattern IS NULL OR x.object_name LIKE :name_pattern ESCAPE '\')
 ORDER BY x.owner, x.object_name
