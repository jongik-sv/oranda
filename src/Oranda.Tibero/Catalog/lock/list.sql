-- Lock 모니터 잠금 목록(Tibero). 열 순서·바인드는 Oracle lock.list 와 같다.
-- V$LOCK(TYPE, ID1, ID2, LMODE, REQUESTED, SESS_ID, CTIME) + V$SESSION. 잠금 종류는 Oracle 두 글자로 바꾼다: WLOCK_DML → TM(ID1 = 객체 번호), WLOCK_TX → TX
-- (그 밖은 Tibero 이름 그대로 — :types 에 없으면 빠진다). 보유 모드 = Tibero LMODE + 1(실측 1 = Row-Shared, Oracle 2; TX 보유 5 → 6).
-- 요청 모드는 원값 그대로(실측 쓰기 IT W1, win144 #60 — TX 대기 요청 원값 6 = Oracle X, +1 하면 7 로 범위 밖). block = 이 잠금을 기다리는
-- 세션이 V$WAITER_SESSION 에 있으면 1. V$LOCK 에 요청 행이 없는 대기자는 V$WAITER_SESSION 으로 채운다. 세션마다 늘 있는 WLOCK_SESS·WLOCK_USER 는 뺀다.
-- 트랜잭션(start_date·used_ublk)은 TX 보유 행에만, V$TRANSACTION.SESS_ID 로 잇는다. start_date 는 서버 시간대로 UTC 로 바꾼다.
-- 확인 필요(쓰기 IT): WLOCK_DML·WLOCK_TX 이름과 ID 뜻, CTIME 단위(초로 봄), MODE_REQ 번호. docs/design/tibero-monitor-catalog.md
-- 바인드: types(',TM,TX,UL,' 꼴), username, schema(TM 행에만), inst_id, min_wait, waiting_only(0/1), max_rows. @PFX@ = dba 또는 all.
-- id: lock.list
-- dbms: tibero
-- min-version: 6
-- requires: v$lock, v$session, v$transaction, v$waiter_session, v$event_name, dba_objects, all_objects
-- license: none
SELECT inst_id, sid, serial#, username, machine, program, type, id1, id2, lmode, request, ctime, block,
       object_owner, object_name, start_date, used_ublk, event, seconds_in_wait, sql_id, prev_sql_id,
       blocking_instance, blocking_session, blocking_serial, status
  FROM (SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) AS inst_id, x.sid, s.serial#, s.username, s.machine,
               s.prog_name AS program, x.type, x.id1, x.id2, x.lmode, x.request, x.ctime, x.block,
               o.owner AS object_owner, o.object_name,
               SYS_EXTRACT_UTC(FROM_TZ(CAST(t.start_time AS TIMESTAMP), TO_CHAR(SYSTIMESTAMP, 'TZH:TZM'))) AS start_date,
               t.used_blk AS used_ublk,
               CASE WHEN e.name IS NOT NULL THEN e.name WHEN s.state = 'RECV_WAITING' THEN 'RECV_WAITING' END AS event,
               CASE WHEN s.wait_event >= 0 THEN TRUNC(s.wait_time / 10) ELSE 0 END AS seconds_in_wait,
               s.sql_id, s.prev_sql_id,
               CASE WHEN bw.hold_sid IS NOT NULL THEN TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')) END AS blocking_instance,
               bw.hold_sid AS blocking_session, b.serial# AS blocking_serial,
               DECODE(s.status, 'RUNNING', 'ACTIVE', 'ASSIGNED', 'ACTIVE', 'READY', 'INACTIVE', s.status) AS status
          FROM (SELECT l.sess_id AS sid, DECODE(l.type, 'WLOCK_DML', 'TM', 'WLOCK_TX', 'TX', l.type) AS type, l.id1, l.id2,
                       CASE WHEN l.lmode > 0 THEN l.lmode + 1 ELSE 0 END AS lmode,
                       CASE WHEN l.requested > 0 THEN l.requested ELSE 0 END AS request, l.ctime,
                       CASE WHEN EXISTS (SELECT 1 FROM v$waiter_session w
                                          WHERE w.hold_sid = l.sess_id AND w.type = l.type AND w.id1 = l.id1 AND w.id2 = l.id2)
                            THEN 1 ELSE 0 END AS block
                  FROM v$lock l
                 WHERE l.type NOT IN ('WLOCK_SESS', 'WLOCK_USER')
                UNION ALL
                SELECT w.wait_sid, DECODE(w.type, 'WLOCK_DML', 'TM', 'WLOCK_TX', 'TX', w.type), w.id1, w.id2, 0,
                       CASE WHEN w.mode_req > 0 THEN w.mode_req ELSE 0 END,
                       (SELECT TRUNC(q.wait_time / 10) FROM v$session q WHERE q.sid = w.wait_sid), 0
                  FROM v$waiter_session w
                 WHERE NOT EXISTS (SELECT 1 FROM v$lock r WHERE r.sess_id = w.wait_sid AND r.requested > 0)) x
          JOIN v$session s ON s.sid = x.sid
          LEFT JOIN v$event_name e ON e.event# = s.wait_event
          LEFT JOIN @PFX@_objects o ON x.type = 'TM' AND o.object_id = x.id1
          LEFT JOIN v$transaction t ON x.type = 'TX' AND x.lmode > 0 AND t.sess_id = x.sid
          LEFT JOIN (SELECT w.wait_sid, MIN(w.hold_sid) AS hold_sid FROM v$waiter_session w GROUP BY w.wait_sid) bw
            ON bw.wait_sid = s.sid
          LEFT JOIN v$session b ON b.sid = bw.hold_sid
         WHERE s.type = 'WTHR'
           AND INSTR(:types, ',' || x.type || ',') > 0
           AND (:username IS NULL OR UPPER(s.username) = UPPER(:username))
           AND (:schema IS NULL OR x.type <> 'TM' OR UPPER(o.owner) = UPPER(:schema))
           AND (:inst_id IS NULL OR :inst_id = TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')))
           AND (:waiting_only = 0 OR x.request > 0 OR x.block > 0)
           AND (:min_wait = 0 OR x.request = 0 OR x.ctime >= :min_wait)
         ORDER BY x.block DESC, x.request DESC, x.ctime DESC, x.sid, x.type)
 WHERE ROWNUM <= :max_rows
