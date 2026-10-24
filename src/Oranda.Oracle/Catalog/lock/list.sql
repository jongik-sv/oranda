-- Lock 모니터 잠금 목록(LK-02·04). gv$lock + gv$session, TM 은 id1 = object_id, TX 는 id1·id2 = xidusn·xidslot·xidsqn 로 gv$transaction.
-- 큐 대기(enq: XX - ...) 세션인데 gv$lock 에 요청 행이 없으면(23ai 의 TM 대기 등) 대기 인자로 요청 행을 채운다: p1 하위 16비트 = 모드, p2·p3 = id1·id2.
-- 백그라운드 세션·시스템 잠금(MR·AE·TO·RT·XR·CF 등)은 항상 뺀다. 기다림 행(request > 0)만 min_wait 로 거르고 막는 행은 남긴다.
-- 바인드: types(',TM,TX,UL,' 꼴), username, schema(TM 행에만), inst_id, min_wait, waiting_only(0/1), max_rows. @PFX@ = dba 또는 all.
-- id: lock.list
-- min-version: 11
-- requires: gv$lock, gv$session, gv$transaction, dba_objects, all_objects
-- license: none
-- rac: gv
SELECT inst_id, sid, serial#, username, machine, program, type, id1, id2, lmode, request, ctime, block,
       object_owner, object_name, start_date, used_ublk, event, seconds_in_wait, sql_id, prev_sql_id,
       blocking_instance, blocking_session, blocking_serial
  FROM (SELECT x.inst_id, x.sid, s.serial#, s.username, s.machine, s.program, x.type, x.id1, x.id2,
               x.lmode, x.request, x.ctime, x.block,
               o.owner AS object_owner, o.object_name,
               t.start_date, t.used_ublk, s.event,
               CASE WHEN s.state = 'WAITING' THEN TRUNC(s.wait_time_micro / 1000000) ELSE 0 END AS seconds_in_wait,
               s.sql_id, s.prev_sql_id, s.blocking_instance, s.blocking_session, b.serial# AS blocking_serial
          FROM (SELECT l.inst_id, l.sid, l.type, l.id1, l.id2, l.lmode, l.request, l.ctime, l.block
                  FROM gv$lock l
                UNION ALL
                SELECT q.inst_id, q.sid, SUBSTR(q.event, 6, 2), q.p2, q.p3, 0, BITAND(q.p1, 65535),
                       TRUNC(q.wait_time_micro / 1000000), 0
                  FROM gv$session q
                 WHERE q.state = 'WAITING'
                   AND q.event LIKE 'enq: __ - %'
                   AND NOT EXISTS (SELECT 1 FROM gv$lock r
                                    WHERE r.inst_id = q.inst_id AND r.sid = q.sid AND r.request > 0)) x
          JOIN gv$session s ON s.inst_id = x.inst_id AND s.sid = x.sid
          LEFT JOIN @PFX@_objects o ON x.type = 'TM' AND o.object_id = x.id1
          LEFT JOIN gv$transaction t ON x.type = 'TX'
                                    AND t.xidusn = TRUNC(x.id1 / 65536) AND t.xidslot = MOD(x.id1, 65536) AND t.xidsqn = x.id2
          LEFT JOIN gv$session b ON b.inst_id = s.blocking_instance AND b.sid = s.blocking_session
         WHERE s.type <> 'BACKGROUND'
           AND x.type NOT IN ('MR', 'AE', 'TO', 'RT', 'XR', 'CF', 'PS', 'TS', 'KT', 'JS', 'DM', 'RS', 'FB', 'CU')
           AND INSTR(:types, ',' || x.type || ',') > 0
           AND (:username IS NULL OR UPPER(s.username) = UPPER(:username))
           AND (:schema IS NULL OR x.type <> 'TM' OR UPPER(o.owner) = UPPER(:schema))
           AND (:inst_id IS NULL OR x.inst_id = :inst_id)
           AND (:waiting_only = 0 OR x.request > 0 OR x.block > 0)
           AND (:min_wait = 0 OR x.request = 0 OR x.ctime >= :min_wait)
         ORDER BY x.block DESC, x.request DESC, x.ctime DESC, x.inst_id, x.sid, x.type)
 WHERE ROWNUM <= :max_rows
