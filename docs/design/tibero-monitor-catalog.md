# Tibero 실행 계획·세션·잠금 카탈로그 (X4, T4 선행)

결정 D-046, 조정 결정24. `src/Oranda.Oracle/Catalog/{plan,session,lock}` 과 같은 id 의 Tibero 판을 `src/Oranda.Tibero/Catalog/{plan,session,lock}` 에 둔다.
근거 = [tibero-catalog-map.md](tibero-catalog-map.md)(뷰 실측표), [tibero-driver-exec.md](tibero-driver-exec.md), 이 문서의 실측(2026-11-09, 6.2.15, 읽기만).

## 1. 계약

- **같은 id = 같은 열(개수·순서·이름)과 같은 바인드 이름.** 호출 쪽(T4 의 Tibero 모니터·계획 서비스)이 Oracle 행 매핑을 그대로 쓸 수 있게 한다. Tibero 에 없는 열은 `CAST(NULL AS …)` 로 자리만 둔다.
- 바인드가 같은지는 `tests/Oranda.Tibero.Tests/Catalog/MonitorCatalogParityTests` 가 단정한다(서버 없이). 열은 서버에서 파일마다 실행해 확인했다(예시 바인드, `@PFX@` = dba·all 둘 다, 23개 파일 모두 성공. Oracle id 28개 = Tibero 판 23 + 없음 5).
- 형식 검사(`SqlCatalog.Validate`)는 `TiberoEmbeddedCatalogTests`·`CombinedCatalogTests` 가 돈다.

## 2. Tibero 판이 없는 id

| id | 없는 것 | 대안 |
|---|---|---|
| `plan.monitor_report_html` | `REPORT_SQL_MONITOR` 에 형식(type) 인수 없음(텍스트 CLOB 만) | `plan.monitor_report`(텍스트)를 보인다 |
| `session.trace_file` | `V$DIAG_INFO` 없음, `V$PROCESS` 에 `TRACEFILE`·`ADDR` 없음 | 없음(추적 파일은 서버 파일) |
| `session.wait_history` | `V$SESSION_WAIT_HISTORY` 없음 | 지금 대기 = `V$SESSION_WAIT`(SID, NAME, ID1, ID2, SEQ#, TIME_WAITED, TIMEOUT), 누적 = `V$SESSION_EVENT` — 「최근 10건」 화면은 Tibero 에서 비활성 + 이유 |
| `lock.deadlocks` | `V$DIAG_ALERT_EXT` 없음 | 없음(알림 로그는 서버 파일) |
| `lock.locked_row` | `*_OBJECTS.DATA_OBJECT_ID` 없음. `DBMS_ROWID.ROWID_CREATE(SEGMENT_NUMBER, ABSOLUTE_FNO, BLOCK_NUMBER, ROW_NUMBER)` 의 세그먼트 번호는 OBJECT_ID 와 다르다(실측: 4303 ≠ 2907) | `ROW_WAIT_OBJ_ID` 가 세그먼트 번호인지 쓰기 IT 로 확인 → 맞으면 `ROWID_CREATE(row_wait_obj_id, row_wait_file_no(절대), row_wait_block_no, row_wait_row_no)` 로 파일을 더한다. 확인 전에는 「잠긴 행 보기」 비활성 |

Tibero 에만 있는 id(짝 Oracle 파일 없음, `MonitorCatalogParityTests.TiberoOnly`): `plan.explain_hash` — Oracle 은 예상 계획 해시를 DBMS_XPLAN 글에서 읽고, Tibero 는 글에 해시 줄이 없어 PLAN_TABLE.SQL_ID·CHILD_NUMBER → V$SQL.PLAN_HASH_VALUE 로 찾는다(P1 M4 실측, win144 #35).

## 3. 공통 바꿈 규칙

| Oracle | Tibero 판 | 근거 |
|---|---|---|
| `gv$…` + `inst_id` | `v$…` + `TO_NUMBER(SYS_CONTEXT('USERENV','INSTANCE'))`(시험 서버 0). `inst_id` 바인드는 `(:inst_id IS NULL OR :inst_id = 그 값)` | `gv$` 는 있으나 단일 인스턴스에서 `INST_ID` 가 NULL(실측). `V$WAITER_SESSION`·`V$BLOCKER_SESSION` 은 `gv$` 없음. TAC 는 나중 |
| `status` ACTIVE/INACTIVE | `DECODE(status, 'RUNNING','ACTIVE', 'READY','INACTIVE', status)` | 실측 값 RUNNING·READY |
| `type` USER/BACKGROUND | `WTHR` → USER, 그 밖 BACKGROUND | 사용자 세션 TYPE = WTHR(81/81) |
| `program` | `prog_name` | |
| `event`·`wait_class` | `V$EVENT_NAME`(EVENT#, NAME, CLASS) 을 `WAIT_EVENT` 번호로 합침. CLASS `STAT_CLASS_*` → Oracle 이름(Idle·Application·Concurrency·User I/O·System I/O·Cluster·Cache·SQL·Other). 클라이언트를 기다리는 세션(`STATE = RECV_WAITING`, WAIT_EVENT −1)은 event `RECV_WAITING`·wait_class `Idle` | `WAIT_EVENT` 는 숫자 |
| `seconds_in_wait`(대기 중이면 wait_time_micro) | `WAIT_EVENT >= 0` 이면 `TRUNC(WAIT_TIME / 10)` | 실측: 쉬는 세션 WAIT_TIME 이 초당 약 9~10 증가(1/10초). 1,604초 = 로그온 뒤 경과와 맞음 |
| `last_call_et` | 실행 중(RUNNING) `TRUNC(SQL_ET / 1000)`, 쉬는 중 `TRUNC(WAIT_TIME / 10)` | 실측: 실행 중 SQL_ET 가 초당 약 900~1000 증가(밀리초), 쉬면 0 |
| `blocking_session`·`blocking_instance` | `V$WAITER_SESSION(WAIT_SID, HOLD_SID, TYPE, ID1, ID2, MODE_REQ, MODE_HELD)` 의 `MIN(HOLD_SID)` | V$SESSION 에 그 열 없음 |
| `final_blocking_session` | `V$WAITER_SESSION` 을 `CONNECT BY NOCYCLE PRIOR hold_sid = wait_sid` 로 올라간 잎(뿌리 보유자) | 같음 |
| `taddr`·`addr` 로 트랜잭션 | `V$TRANSACTION.SESS_ID = V$SESSION.SID` | |
| `xidusn.xidslot.xidsqn`, `used_ublk`, `used_urec`, `start_date` | `usn.slot.wrap`, `used_blk`, `used_rec`, `start_time` | |
| 잠금 종류 TM·TX | `DECODE(type, 'WLOCK_DML','TM', 'WLOCK_TX','TX', type)` | 실측(4장 1): 행 갱신 대기에서 WLOCK_DML·WLOCK_TX. 그 밖에 WLOCK_DD_OBJ(보유 1)가 보이나 TM·TX·UL 이 아니라 목록에서 빠진다 |
| 잠금 모드 번호 | 보유 = Tibero LMODE + 1(0 은 0), 요청 = 원값 그대로 | 실측: DBA_DDL_LOCKS 「Row-Shared(RS)」 = V$LOCK LMODE 1(Oracle RS = 2). WLOCK_SESS 보유 5 = X. 쓰기 IT(win144 #60·#63, main 568db678): TX 보유 LMODE 5 → 6(X), DML 보유 2 → 3(SX), 대기 세션의 V$LOCK.REQUESTED·V$WAITER_SESSION.MODE_REQ = 6(이미 Oracle 체계, +1 하면 7 로 범위 밖) |
| `V$LOCK.SID`·`REQUEST`·`BLOCK` | `SESS_ID`·`REQUESTED`·`V$WAITER_SESSION` 에 그 잠금(종류·ID1·ID2)을 기다리는 행이 있으면 1 | |
| 세션 통계 `v$sesstat` + `v$statname` 조인 | `V$SESSTAT` 에 NAME 이 있어 조인 없음(STATISTIC# 은 TBR-8026) | |
| 통계 이름 | CPU = `'DB CPU time'/10000`(마이크로초 → 1/100초), logical reads = `'logical reads'`, redo = `'redo log size'` | driver-exec 6장 표 |
| `v$sess_io` | `V$SESSION_IO`(같은 열) | |
| `sql_fulltext` | `V$SQL.SQL_TEXT`(전문: 10,957자 문장의 길이 = `V$SQLTEXT` 조각 합) | |
| `DBMS_XPLAN.DISPLAY_CURSOR` 의 `plan_table_output` | `column_value AS plan_table_output`. 인수 (IN_SQL_ID, IN_CHILD_NO, FORMAT), TYPICAL·BASIC·ALLSTATS LAST 모두 됨 | 줄 모양이 Oracle 과 다르다: 머리 「SQL ID : …」, 자식 번호 줄 없음 → `plan.last_cursor_text` 의 머리를 읽는 쪽은 Tibero 모양을 따로 읽는다 |
| `DBMS_XPLAN.DISPLAY` | 없음 → `plan.explain_text` 는 PLAN_TABLE 을 들여쓰기 표(ID·Operation·Name·Cost·Cards + Access·Filter 줄)로 직접 그린다 | |
| `V$SQL_PLAN_STATISTICS_ALL` | `V$SQL_PLAN` + `V$SQL_PLAN_STATISTICS`(SQL_ID·CHILD_NUMBER·ID). OPTIONS 열 없음(OPERATION 이 「index (full)」처럼 방식을 담음) → options NULL | 실행 통계는 `GATHER_SQL_PLAN_STAT=YES` 일 때만(시험 서버 NO) |
| `REPORT_SQL_MONITOR(sql_id =>, type =>)` | `REPORT_SQL_MONITOR(:sql_id)`(CLOB). `license: none`(Tibero 는 팩 없음) | |
| `V$LOCKED_OBJECT` | `V$LOCK` 의 WLOCK_DML(ID1 = 객체 번호) | 동의어 순환으로 못 읽음 |
| `MAX_IDLE_BLOCKER_TIME` | NULL(해당 매개변수 없음, `V$PARAMETERS` 실측) | |
| `V$OPEN_CURSOR.CURSOR_TYPE`·`LAST_SQL_ACTIVE_TIME`, `V$SESSION_LONGOPS.TIME_REMAINING`·`MESSAGE`, `V$SQL_BIND_CAPTURE.WAS_CAPTURED` | NULL(was_captured 는 캡처 시각이 있으면 YES). 열린 커서는 CURSOR_ID 내림차순 | |
| UNION ALL 갈래에 바인드를 그대로 열로 | `CAST(:x AS …)` | 안 하면 TBR-11022(`lock.waiting_binds` 실측) |
| `DBA_DDL_LOCKS` | `SYS.DBA_DDL_LOCKS`. 모드 글은 Tibero 모양(「Row-Shared(RS)」). 걸러 내는 보유 = None·Row-Shared(RS) | PUBLIC 동의어 순환 |

## 4. 쓰기 IT(D3, win144)에서 확인할 것

시험 서버에 잠금·대기가 없어 값이 비어 있는 경로다. 두 세션 + `ORT_` 표로 행 잠금 대기를 만들어 본다.
실측 = tb-mon P7 쓰기 IT `TiberoLockWriteIt.W1`(같은 시험 계정 세션 둘이 `ORT_LKW_BLOCK` 한 행을 갱신, win144 #60·#63, main 568db678). 시험 계정은 performance views = 볼 수 있음, dictionary = 못 봄.

1. **닫음.** 표 DML 잠금 = `WLOCK_DML`, ID1 = 객체 번호(`ALL_OBJECTS.OBJECT_ID` 로 이으면 `ORT_LKW_BLOCK`), ID2 는 세션마다 0·2 로 달라 뜻 모름(쓰지 않음). 트랜잭션 잠금 = `WLOCK_TX`, 보유자·대기자의 ID1·ID2 가 같고 V$WAITER_SESSION 의 TYPE·ID1·ID2 와도 같다(block 판정이 맞게 켜짐)
2. **반만 닫음.** `MODE_REQ` = `V$LOCK.REQUESTED` 와 같은 값(6 = X)이고 Oracle 체계다 — LMODE(0 기반)와 체계가 다르다 → `lock.list` 는 요청 모드에 +1 하지 않는다. `V$LOCK.CTIME` 단위는 대기가 1초 안이라 0 만 보여 아직 모름(초로 봄)
3. **반만 닫음.** 대기 세션 이벤트 = `WE_WLOCK_TX`, 블로킹 트리·`session.my_blocking`·`lock.blocker_impact`·`lock.idle_transactions` 가 대기를 보인다. 대기 초(`WAIT_TIME`)는 대기가 짧아 0. `ROW_WAIT_OBJ_ID` 는 재지 않았다(`lock.locked_row` 는 없음 그대로)
4. 확인 필요 그대로: `V$TRANSACTION.STATE` 번호의 뜻
5. 확인 필요 그대로: `V$SESSION_LONGOPS.ELAPSED_TIME` 단위(긴 작업이 있을 때)
6. 확인 필요 그대로: `final_blocking_session` 계산(3단 대기 사슬 — 세션 둘로는 못 만든다)

그 밖의 실측(win144 #58·#63): `SYS_CONTEXT('USERENV','INSTANCE')` = 0(세션 키·inst_id 가 0). `V$SQL_BIND_CAPTURE` 는 바인드를 쓴 문장도 0행 → `lock.waiting_binds`·`sql.binds` 는 빈 결과가 정상.
일반 계정의 `ALTER SYSTEM KILL SESSION` = TBR-17004 → 권한 부족으로 분류, SERIAL# 이 2^31 이상인 문장도 그 단계까지 파싱된다(`TiberoMonitorKillIt.K1`). 권한 계정 Kill(K2)은 시험 서버에 권한 계정이 없어 아직 안 돌렸다.

## 5. tuning(실행 계획·통계 쪽 9개)

`tuning/` 21개 중 9개를 했다. 서버 실행 9/9 성공(예시 표 = 시험 계정의 통계 있는 표, 계획 = `/*+ FULL */` 질의의 커서).

| id | Tibero 판 |
|---|---|
| `tuning.prev_cursor` | 내 SID = `V$MYSTAT`(USERENV 'SID' 는 TBR-11006) |
| `tuning.child_cursors` | `V$SQL`. 바인드 민감·인식 열 없음 → NULL |
| `tuning.sql_stats` | `V$SQL` 합. `PARSING_SCHEMA_ID` → `ALL_USERS.USERNAME`. instances = 0/1 |
| `tuning.table_stats` | `*_TAB_STATISTICS` 없음 → `@PFX@_TABLES` 통계 열. stale_stats NULL |
| `tuning.index_stats` | `@PFX@_INDEXES`. `*_IND_STATISTICS` 없음 → stale_stats NULL |
| `tuning.column_stats` | `@PFX@_TAB_COL_STATISTICS`(소유자 열 = TABLE_OWNER). HISTOGRAM 열 없음 → 버킷 2개 이상이면 `HISTOGRAM`, 아니면 `NONE` |
| `tuning.histogram`·`tuning.index_columns` | Oracle 과 같은 열(본문 그대로) |
| `tuning.recommend_plan` | `V$SQL_PLAN.OPERATION`(「table access (full)」) 을 Oracle 모양 operation·options(`TABLE ACCESS`/`FULL`, `INDEX`/`FULL SCAN`, `CARTESIAN`)로 나눔. 실측 operation 62종 목록에 카티션이 없어 `%(cartesian)%` 은 짐작(쓰기 IT 또는 카티션 질의로 확인) |

## 6. X4b: tuning 나머지 12개, sql 3개, app 9개

T4 화면(실행 계획·세션·Lock 모니터·튜닝·Top SQL)이 쓰는 `sql.*`·`app.*` 는 모두다(App 코드 검색). id 24개 = Tibero 판 14개(tuning 6·sql 3·app 5) + 없음 10개. 서버 실행: 질의 11/11 성공, `ALTER`·`GRANT` 3개는 아래처럼 따로 확인.

| id | Tibero 판 |
|---|---|
| `tuning.ash_waits` | `V$ACTIVE_SESSION_HISTORY`: SESSION_STATE·EVENT·WAIT_CLASS 없음 → `WAIT_EVENT < 0` = 'ON CPU', 그 밖 `V$EVENT_NAME`. 시험 서버 표본 0행 |
| `tuning.awr_plan_history`·`awr_plan_steps` | TPR(`DBA_HIST_SQLSTAT`·`SNAPSHOT`·`SQL_PLAN`)은 SQL_ID 대신 **SQL_HASH_VALUE**. sql_id(13자 base32)의 아래 32비트 = hash value(Oracle 과 같은 규칙, V$SQL 두 건 일치 확인) → SQL 안 `CONNECT BY` 로 계산. DBID·*_DELTA 없음. 값은 **스냅숏 구간 값**(실측: 같은 SQL 의 EXECUTIONS 가 54→333→54→45 로 오르내림 = 누적 아님) → 그대로 합한다. 계획 단계는 options NULL(operation 에 방식 포함) |
| `tuning.recommend_candidates` | `V$SQLSTATS` 의 ELAPSED_TIME·CPU_TIME 등이 음수가 부호 없는 64비트로 넘어간 값(18446744073…)을 준다(실측) → `V$SQL` 을 sql_id·plan_hash 로 묶는다. version_count = 자식 수, fms NULL |
| `tuning.report_env` | VERSION = V$VERSION 행(PRODUCT_MAJOR·BUILD_NUMBER·STABLE_VERSION)을 한 줄 배너로. PARAMETER = `V$PARAMETERS` 의 Tibero 이름(OPTIMIZER_MODE·CURSOR_SHARING·GATHER_SQL_PLAN_STAT·MEMORY_TARGET 등, 소문자로). NLS = `NLS_DATABASE_PARAMETERS`(문자 집합 행 없음) |
| `tuning.wait_sample` | V$SESSION + V$EVENT_NAME, 내 SID = V$MYSTAT, 실행 중 = RUNNING |
| `sql.binds`·`sql.full_text`·`sql.top` | `lock.waiting_binds`·`session.sql_text` 와 같은 규칙. top 의 파싱 스키마 = `PARSING_SCHEMA_ID` → `ALL_USERS`. `FETCH FIRST :top ROWS ONLY` 바인드 됨 |
| `app.fk_index_check`·`app.locked_row_select` | Oracle 본문 그대로(사전 열 같음, ROWID 글 비교 읽힘) |
| `app.kill_session` | `ALTER SYSTEM KILL SESSION (@ID@)`(괄호, IMMEDIATE 없음), @ID@ = 'sid,serial#'. 일반 계정 = TBR-17004 권한 없음까지 파싱 → 실제 종료는 권한 계정 IT |
| `app.statistics_level` | `STATISTICS_LEVEL` 은 TBR-7001 → `ALTER SESSION SET GATHER_SQL_PLAN_STAT = Y`(켠 뒤 ALLSTATS LAST 에 CR Gets 찬 것 확인) |
| `app.ddl_locks_grant` | 같은 GRANT(끝 `;` 뺌). 권한 부여라 실행 안 함 |

| 없음 | 이유 | 대안 |
|---|---|---|
| `tuning.plan_controls` | SQL 프로필·기준선·패치 뷰 없음, signature 열 없음 | 화면 비활성 + 이유 |
| `tuning.recommend_literals` | FORCE_MATCHING_SIGNATURE 없음 | 리터럴 묶음 추천은 Tibero 에서 끔(`CURSOR_SHARING` 값은 report_env 로 보임) |
| `tuning.shared_cursor` | `V$SQL_SHARED_CURSOR` 없음 | 자식 수만(`child_cursors`) |
| `tuning.trace_file`·`trace_file_self`·`trace_lines` | `V$PROCESS.TRACEFILE`·`V$DIAG_*` 없음(추적 파일은 서버 파일) | 없음 |
| `app.disconnect_session` | `ALTER SYSTEM DISCONNECT SESSION` = TBR-7001 | 세션 끊기는 KILL 하나만 |
| `app.trace_event_on`·`off`·`trace_parse` | 10053 이벤트 = TBR-7001(옵티마이저 추적은 `OPTIMIZER_TRACE` 매개변수가 있으나 결과가 서버 파일이라 읽을 길 없음) → 파싱만 하는 블록도 쓸 데 없음 | 10053 보기 비활성 + 이유 |

`MonitorCatalogParityTests` 가 여섯 폴더(plan·session·lock·tuning·sql·app)의 바인드·`@KEY@` 자리가 Oracle 과 같은지와 없음 15개를 단정한다.
