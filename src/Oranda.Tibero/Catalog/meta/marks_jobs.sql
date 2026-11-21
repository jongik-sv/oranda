-- 스키마 브라우저 작업(JOB) 표식. 바인드: owner. 열(ObjectMarkRows.Job): job_name, enabled(TRUE/FALSE), state, job_kind(DBMS_JOB 이면 'DBMS_JOB').
-- 스케줄러 잡은 Oracle 판(meta.marks_jobs)과 같은 규칙: 켜져 있고 예약된(SCHEDULED) 것은 기본이라 뺀다.
-- DBMS_JOB(2026-11-21 Tibero 6 실측, 목록 = meta.jobs)은 모두 낸다(종류 표식 DBMS_JOB): broken = 'Y' → BROKEN, failures > 0 → FAILED, 그 밖은 상태 없음.
-- 이름 = meta.jobs 와 같다(*_jobs_with_name.name, 없으면 job 번호 글). 소유자 = schema_user(사용자 번호) → all_users.username.
-- id: meta.marks_jobs
-- dbms: tibero
-- min-version: 6
-- requires: dba_scheduler_jobs, all_scheduler_jobs, dba_jobs, all_jobs, dba_jobs_with_name, all_jobs_with_name
-- license: none
SELECT j.job_name, j.enabled, j.state, CAST(NULL AS VARCHAR2(8)) AS job_kind
  FROM @PFX@_scheduler_jobs j
 WHERE j.owner = :owner
   AND (j.enabled <> 'TRUE' OR j.state <> 'SCHEDULED')
UNION ALL
SELECT NVL(n.name, TO_CHAR(d.job)), 'TRUE',
       CASE WHEN d.broken = 'Y' THEN 'BROKEN' WHEN d.failures > 0 THEN 'FAILED' END,
       'DBMS_JOB'
  FROM @PFX@_jobs d
  JOIN all_users u ON u.user_id = d.schema_user
  LEFT JOIN @PFX@_jobs_with_name n ON n.job = d.job
 WHERE u.username = :owner
   AND (n.name IS NULL
        OR NOT EXISTS (SELECT 1 FROM @PFX@_scheduler_jobs j WHERE j.owner = u.username AND j.job_name = n.name))
