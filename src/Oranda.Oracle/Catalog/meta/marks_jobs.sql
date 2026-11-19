-- 스키마 브라우저 작업(스케줄러 JOB) 표식(꺼짐·실행 중·실패·BROKEN). 바인드: owner. 켜져 있고 예약된(SCHEDULED) 작업은 기본이라 뺀다.
-- 열(ObjectMarkRows.Job): job_name, enabled(TRUE/FALSE), state(DISABLED·RUNNING·FAILED·BROKEN·RETRY SCHEDULED·CHAIN_STALLED·COMPLETED …).
-- DBMS_JOB 작업(all_jobs)은 객체 목록(all_objects)에 없어 다루지 않는다.
-- id: meta.marks_jobs
-- min-version: 11
-- requires: dba_scheduler_jobs, all_scheduler_jobs
-- license: none
SELECT j.job_name, j.enabled, j.state
  FROM @PFX@_scheduler_jobs j
 WHERE j.owner = :owner
   AND (j.enabled <> 'TRUE' OR j.state <> 'SCHEDULED')
