-- 작업(JOB) 하나의 속성(객체 상세 「일반」·DBMS_JOB DDL). 바인드: owner, name(meta.jobs 의 이름 — 스케줄러 잡 이름, DBMS_JOB 은 이름 또는 job 번호 글).
-- 열: [0] job_kind(SCHEDULER | DBMS_JOB) [1] job(DBMS_JOB 번호) [2] enabled [3] state [4] next_run [5] run_interval [6] what [7] failures
--     [8] last_run [9] broken(Y/N) [10] job_type [11] comments. 시각은 'YYYY-MM-DD HH24:MI:SS' 글(두 갈래의 형식이 DATE·TIMESTAMP WITH TIME ZONE 으로 달라서).
-- 2026-11-21 Tibero 6 실측: all_jobs·all_jobs_with_name 열 = job, (name,) schema_user, next_date, interval, broken, failures, what, instance, last_date, this_date.
-- id: meta.job_detail
-- dbms: tibero
-- min-version: 6
-- requires: dba_scheduler_jobs, all_scheduler_jobs, dba_jobs, all_jobs, dba_jobs_with_name, all_jobs_with_name
-- license: none
SELECT 'SCHEDULER' AS job_kind, CAST(NULL AS NUMBER) AS job, j.enabled, j.state,
       TO_CHAR(j.next_run_date, 'YYYY-MM-DD HH24:MI:SS') AS next_run, j.repeat_interval AS run_interval, j.job_action AS what,
       j.failure_count AS failures, TO_CHAR(j.last_start_date, 'YYYY-MM-DD HH24:MI:SS') AS last_run,
       CAST(NULL AS VARCHAR2(1)) AS broken, j.job_type, j.comments
  FROM @PFX@_scheduler_jobs j
 WHERE j.owner = :owner
   AND j.job_name = :name
UNION ALL
SELECT 'DBMS_JOB', d.job, CAST(NULL AS VARCHAR2(5)), CAST(NULL AS VARCHAR2(15)),
       TO_CHAR(d.next_date, 'YYYY-MM-DD HH24:MI:SS'), d.interval, d.what,
       d.failures, TO_CHAR(d.last_date, 'YYYY-MM-DD HH24:MI:SS'),
       d.broken, CAST(NULL AS VARCHAR2(30)), CAST(NULL AS VARCHAR2(240))
  FROM @PFX@_jobs d
  JOIN all_users u ON u.user_id = d.schema_user
  LEFT JOIN @PFX@_jobs_with_name n ON n.job = d.job
 WHERE u.username = :owner
   AND NVL(n.name, TO_CHAR(d.job)) = :name
