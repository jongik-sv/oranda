-- 세션이 잠근 객체(Tibero). V$LOCKED_OBJECT 가 없어(동의어 순환) V$LOCK 의 DML 잠금(WLOCK_DML, ID1 = 객체 번호)으로 대신한다.
-- 잠금 모드는 Tibero 번호(1 = Row-Shared, 실측 DBA_DDL_LOCKS 대조) + 1 이 Oracle 번호라서 Oracle 과 같은 이름으로 바꾼다.
-- WLOCK_DML 이름·ID1 뜻은 쓰기 IT 로 확인할 것(docs/design/tibero-monitor-catalog.md). @PFX@ = dba 또는 all. 바인드: inst_id, sid
-- id: session.locks
-- dbms: tibero
-- min-version: 6
-- requires: v$lock, dba_objects, all_objects
-- license: none
SELECT o.owner, o.object_name, o.object_type,
       DECODE(l.lmode, 0, 'None', 1, 'Row-S (SS)', 2, 'Row-X (SX)', 3, 'Share',
              4, 'S/Row-X (SSX)', 5, 'Exclusive', TO_CHAR(l.lmode)) AS locked_mode
  FROM v$lock l
  LEFT JOIN @PFX@_objects o ON o.object_id = l.id1
 WHERE (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
   AND l.sess_id = :sid
   AND l.type = 'WLOCK_DML'
   AND l.lmode > 0
 ORDER BY o.owner, o.object_name
