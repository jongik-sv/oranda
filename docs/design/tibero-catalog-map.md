# Tibero 사전 뷰 실측표 (T3 준비)

결정 D-046, 설계 [tibero.md](tibero.md). `src/Oranda.Oracle/Catalog/**/*.sql` 이 쓰는 사전 뷰·`V$`/`GV$`·`DBMS_*` 가 Tibero 에 있는지, 열 이름이 어떻게 다른지를 시험 서버에서 읽기 전용으로 잰 결과다. T3(카탈로그·스키마 브라우저·객체 상세) 가 Tibero 용 `.sql` 을 쓸 때의 입력이다. 이 문서는 조사 결과만 담는다. 코드는 바꾸지 않는다.

## 1. 측정 조건

- 서버: Tibero 6(`V$VERSION` PRODUCT_MAJOR=6, 빌드 302172, 프로토콜 2.15), 에디션 standard, 아카이브 모드. 측정일 2026-11-09. Tibero 7 은 서버가 없어 못 쟀다.
- 계정: 일반 사용자 계정(CONNECT, RESOURCE 역할과 SELECT ANY TABLE 권한). DBA 계정이 아니다. 그런데도 `DBA_*`·`V$`·`GV$` 는 읽혔다. 권한 때문에 «없음» 으로 보이는 뷰가 섞였을 가능성은 낮지만(없는 뷰는 모두 `ODP-8033 객체 없음` 이고 권한 오류가 아니다), DBA 계정으로 한 번 더 확인할 여지는 있다.
- 방법: ① `SELECT * FROM <뷰> WHERE 1=0` 으로 존재와 열 목록 ② 카탈로그 `.sql` 마다 `@PFX@` 를 `dba`·`all` 로 풀어 바인드를 전부 NULL 로 두고 그대로 실행(파싱 오류의 첫 줄만 기록) ③ `alias.열` 참조를 뽑아 ①의 열 목록과 대조(자동 대조 + 사람이 읽고 정리) ④ `ALL_OBJECTS`·`ALL_PROCEDURES`·`ALL_ARGUMENTS` 로 `DBMS_*` 패키지와 프로시저 확인 ⑤ 함수·구문 몇 개를 `FROM DUAL` 로 시험. DML·DDL·ALTER·세션 끊기는 하지 않았다. 접속 정보는 이 문서에 넣지 않는다.
- 한계: ②는 «파싱이 되는가» 만 본다. 파싱이 돼도 값의 뜻·단위(예: `WAIT_TIME`, `ELAPSED_TIME`)가 Oracle 과 같다는 뜻은 아니다. 첫 오류에서 멈추므로 ③ 이 열 단위 목록의 근거다. ③ 은 별칭 해석이 단순해서 서브쿼리 안의 같은 별칭을 놓칠 수 있다. 열 이름을 `alias.` 없이 쓴 경우(예: `dbinfo.column_search` 의 단독 열)는 대조하지 못했다. 실행 결과(행 값)는 보지 않았다.
- `{table}` 같은 자리표시자를 쓰는 문장과 `ALTER`·`GRANT`·`DECLARE` 로 시작하는 문장은 SELECT 로 시험하지 못했다(6장 표에 «미점검/문장 아님»).

## 2. 요약

- 카탈로그 파일 132개가 쓰는 사전 뷰·V$ 이름(`DBA_/ALL_/USER_` 접두와 `V$/GV$` 는 하나로 묶음) 73개 중: **그대로 있음 19 / 있으나 열이나 조건이 다름 36 / 없음 또는 이름이 다름 18**.
- 파일 단위: 그대로 파싱됨 38 / 차이 있음 86 / 문장 아님 8. 그대로 파싱된 파일도 값의 뜻은 따로 봐야 한다.
- 가장 크게 다른 곳: **세션·잠금·대기 계열**. Tibero `V$SESSION` 에는 `EVENT`·`WAIT_CLASS`·`BLOCKING_SESSION`·`PROGRAM`·`TADDR`·`PADDR` 가 없다(각각 `WAIT_EVENT`·없음·`V$WAITER_SESSION` 으로 대신·`PROG_NAME`·`V$TRANSACTION.SESS_ID` 조인·`PID`). 세션 모니터·잠금·차단 트리·유휴 트랜잭션 문장이 모두 새로 써야 한다.
- **통계·튜닝 계열**: `*_TAB_STATISTICS`·`*_IND_STATISTICS`·`DBA_SQL_PROFILES`·`DBA_SQL_PLAN_BASELINES`·`GV$SQL_SHARED_CURSOR`·`V$DIAG_*` 가 없다. 대신 `*_TABLES`·`*_INDEXES` 의 통계 열을 쓴다. `GV$SQL` 에는 `SQL_FULLTEXT`·`PARSING_SCHEMA_NAME`·bind 인식·시그니처 열이 없다. AWR(`DBA_HIST_*`)은 있으나 `SQL_ID` 대신 `SQL_HASH_VALUE`, `*_DELTA` 열 없음.
- **메타데이터 계열은 대체로 그대로**: `*_INDEXES`·`*_IND_COLUMNS`·`*_CONS_COLUMNS`·`*_SOURCE`·`*_ERRORS`·`*_TRIGGERS`·`*_MVIEW_LOGS`·`*_TAB_COMMENTS`·`*_COL_COMMENTS`·`*_TAB_COLUMNS`·`*_USERS` 는 열까지 같다. 다른 곳은 `*_OBJECTS`(EDITIONABLE·DATA_OBJECT_ID 없음), `*_TABLES`(CLUSTER_NAME 없음), `*_TAB_COLS`(IDENTITY_COLUMN·USER_GENERATED 없음), `*_DEPENDENCIES`(열 이름 다름), `*_CONSTRAINTS`(SEARCH_CONDITION_VC 없음), `*_TAB_PARTITIONS`·`*_SYNONYMS`·`*_PROCEDURES`·`*_ARGUMENTS`.
- 패키지: `DBMS_METADATA.GET_DDL`·`DBMS_JAVA.LONGNAME/SHORTNAME`·`DBMS_XPLAN.DISPLAY_CURSOR`·`DBMS_SQL`·`DBMS_STATS` 있음. `DBMS_METADATA.SESSION_TRANSFORM/SET_TRANSFORM_PARAM/GET_DEPENDENT_DDL`·`DBMS_XPLAN.DISPLAY`·`DBMS_SQL.NATIVE` 는 프로시저 목록에 없음. `DBMS_SQLTUNE.REPORT_SQL_MONITOR` 는 있으나 인수가 달라 Oracle 호출 형태로는 안 맞음. `DBMS_ROWID.ROWID_CREATE` 는 있으나 Oracle 호출 형태로는 안 맞음. `DBMS_MONITOR` 없음.
- 함수·구문: `FETCH FIRST n ROWS ONLY`·`OFFSET … FETCH`·`LISTAGG … WITHIN GROUP`·`COALESCE`·`CAST`·`REGEXP_*`(WHERE 절)·`SYS_EXTRACT_UTC` 됨. `SYS_CONTEXT('USERENV', …)` 는 `SESSION_USER`·`CURRENT_SCHEMA`·`INSTANCE`·`DB_NAME`·`LANG`·`SESSIONID` 만 되고 `SID`·`CON_NAME`·`SERVICE_NAME`·`DB_UNIQUE_NAME` 은 `ODP-11006` 오류. `SELECT REGEXP_LIKE(…)` (선택 목록 안)과 `JSON_VALUE` 는 함수 없음.

## 3. 사전 뷰 표

«판정»: 있음 = 이름과 사용한 열이 모두 있음 / 있음, 열 다름 = 뷰는 있으나 카탈로그가 쓰는 열 일부가 없음(4장) / 이름 다름 / 없음. «카탈로그 id» 는 그 뷰를 쓰는 id 전체다(Oracle 11·23 용 변형 파일은 같은 id).

### 3.1 없음 또는 이름 다름

| 뷰 | 판정 | 카탈로그 id | 대체 |
|---|---|---|---|
| `*_IND_STATISTICS` | 없음 | tuning.index_stats | 없음. 인덱스 통계는 *_INDEXES(BLEVEL,LEAF_BLOCKS,DISTINCT_KEYS,CLUSTERING_FACTOR,NUM_ROWS,LAST_ANALYZED) 에 있음 |
| `*_JAVA_DERIVATIONS` | 없음 | meta.java_source_classes, meta.java_source_object | 없음 |
| `*_JAVA_IMPLEMENTS` | 없음 | meta.java_implements | 없음 |
| `*_JAVA_INNERS` | 없음 | meta.java_inners | 없음 |
| `*_SQL_PATCHES` | 없음 | tuning.plan_controls | 없음 |
| `*_SQL_PLAN_BASELINES` | 없음 | tuning.plan_controls | 없음(PUBLIC 동의어만, 대상 없음) |
| `*_SQL_PROFILES` | 없음 | tuning.plan_controls | 없음 |
| `*_TAB_STATISTICS` | 없음 | tuning.table_stats | 없음. 표 통계는 *_TABLES(NUM_ROWS,BLOCKS,AVG_ROW_LEN,SAMPLE_SIZE,LAST_ANALYZED) 에 있음. 내부 뷰 _TAB_STATISTICS 는 비공개 |
| `V$DIAG_ALERT_EXT` | 없음 | lock.deadlocks | 없음(알림 로그는 서버 파일) |
| `V$DIAG_INFO` | 없음 | session.trace_file, tuning.trace_file_self | 없음(진단 디렉터리 개념 없음) |
| `V$DIAG_TRACE_FILE_CONTENTS` | 없음 | tuning.trace_lines | 없음(추적 파일은 서버 파일) |
| `V$LOCKED_OBJECT` | 없음 | session.locks | 뷰 없음(PUBLIC 동의어만 있고 가리키는 대상이 순환). V$LOCK(SESS_ID,TYPE,ID1,ID2) + DBA_OBJECTS 로 대신 |
| `V$OPTION` | 없음 | dbinfo.options | 없음. 가장 가까운 것 = V$LICENSE(PRODUCT_NAME,PRODUCT_VERSION,EDITION,…) |
| `V$PARAMETER` | 이름 다름 | dbinfo.flashback_env, dbinfo.parameters, session.my_blocking, tuning.report_env | 이름 다름 → V$PARAMETERS(NAME,VALUE,DFLT_VALUE), V$SYSTEM_PARAMETERS(NAME,VALUE). 설명·유형·변경 가능 여부 열 없음 |
| `V$SESS_IO` | 이름 다름 | session.io | 이름 다름 → V$SESSION_IO / GV$SESSION_IO(SID,BLOCK_GETS,CONSISTENT_GETS,PHYSICAL_READS,BLOCK_CHANGES,CONSISTENT_CHANGES) |
| `V$SESSION_WAIT_HISTORY` | 없음 | session.wait_history | 없음. 현재 대기 = V$SESSION_WAIT, 누적 = V$SESSION_EVENT |
| `V$SQL_PLAN_STATISTICS_ALL` | 이름 다름 | plan.cursor_steps | 이름 다름 → V$SQL_PLAN_STATISTICS(ID,STARTS,OUTPUT_ROWS,ELAPSED_TIME,CR_BUFFER_GETS,…) 와 V$SQL_PLAN 을 SQL_ID·CHILD_NUMBER·ID 로 합침 |
| `V$SQL_SHARED_CURSOR` | 없음 | tuning.shared_cursor | 없음 |

### 3.2 있으나 열이나 조건이 다름

| 뷰 | 판정 | 카탈로그 id | 비고(열 차이는 4장) |
|---|---|---|---|
| `*_ARGUMENTS` | 있음, 열 다름 | meta.arguments | ALL_ARGUMENTS·USER_ARGUMENTS 만 있고 DBA_ARGUMENTS 는 없음 |
| `*_CONSTRAINTS` | 있음, 열 다름 | app.fk_index_check, meta.constraint_columns, meta.constraints, meta.detail_referenced_by |  |
| `*_DDL_LOCKS` | 있음(조건) | app.ddl_locks_grant, lock.ddl_locks | SYS.DBA_DDL_LOCKS 는 있음(SESSION_ID,OWNER,NAME,TYPE,MODE_HELD,MODE_REQUESTED, Oracle 과 같음). PUBLIC 동의어가 순환이라 `SYS.` 를 붙여야 읽힘. DBA_ 만 있고 ALL_·USER_ 는 해당 없음 |
| `*_DEPENDENCIES` | 있음, 열 다름 | dbinfo.column_reorder_source, dbinfo.references, meta.detail_dependencies |  |
| `*_HIST_SNAPSHOT` | 있음, 열 다름 | tuning.awr_plan_history |  |
| `*_HIST_SQL_PLAN` | 있음, 열 다름 | tuning.awr_plan_steps |  |
| `*_HIST_SQLSTAT` | 있음, 열 다름 | tuning.awr_plan_history |  |
| `*_JAVA_ARGUMENTS` | 있음, 열 다름 | meta.java_arguments |  |
| `*_JAVA_CLASSES` | 있음, 열 다름 | meta.java_class, meta.java_source_classes |  |
| `*_JAVA_FIELDS` | 있음, 열 다름 | meta.java_fields |  |
| `*_JAVA_METHODS` | 있음, 열 다름 | meta.java_methods |  |
| `*_OBJECTS` | 있음, 열 다름 | dbinfo.column_search, dbinfo.flashback_ddl, dbinfo.object_search, lock.list, lock.locked_row, meta.detail_general, meta.java_available, meta.java_objects, meta.java_source_object, meta.name_step, meta.objects, session.blocking_tree, session.locks |  |
| `*_PROCEDURES` | 있음, 열 다름 | meta.procedures |  |
| `*_RECYCLEBIN` | 있음, 열 다름 | dbinfo.recyclebin |  |
| `*_SYNONYMS` | 있음, 열 다름 | meta.name_step |  |
| `*_TAB_COL_STATISTICS` | 있음, 열 다름 | tuning.column_stats |  |
| `*_TAB_COLS` | 있음, 열 다름 | meta.columns |  |
| `*_TAB_PARTITIONS` | 있음, 열 다름 | meta.detail_partitions |  |
| `*_TAB_PRIVS` | 있음, 열 다름 | meta.detail_privileges, meta.detail_privileges_user |  |
| `*_TABLES` | 있음, 열 다름 | dbinfo.column_reorder_traits, meta.detail_stats, tuning.recommend_plan |  |
| `V$ACTIVE_SESSION_HISTORY` | 있음, 열 다름 | tuning.ash_waits |  |
| `V$DATABASE` | 있음, 열 다름 | dbinfo.database, tuning.awr_plan_steps |  |
| `V$INSTANCE` | 있음, 열 다름 | dbinfo.instance |  |
| `V$LOCK` | 있음, 열 다름 | lock.idle_transactions, lock.list |  |
| `V$OPEN_CURSOR` | 있음, 열 다름 | session.open_cursors |  |
| `V$PROCESS` | 있음, 열 다름 | session.trace_file, tuning.trace_file |  |
| `V$SESSION` | 있음, 열 다름 | lock.blocker_impact, lock.ddl_locks, lock.idle_transactions, lock.list, lock.locked_row, lock.waiting_binds, session.blocking_tree, session.detail, session.list, session.my_blocking, session.trace_file, session.transaction, tuning.prev_cursor, tuning.trace_file, tuning.wait_sample |  |
| `V$SESSION_LONGOPS` | 있음, 열 다름 | session.longops |  |
| `V$SESSTAT` | 있음, 열 다름 | session.list, session.stats |  |
| `V$SQL` | 있음, 열 다름 | session.sql_text, sql.full_text, sql.top, tuning.child_cursors, tuning.plan_controls, tuning.prev_cursor, tuning.recommend_candidates, tuning.recommend_literals, tuning.sql_stats |  |
| `V$SQL_BIND_CAPTURE` | 있음, 열 다름 | lock.waiting_binds, sql.binds |  |
| `V$SQL_PLAN` | 있음, 열 다름 | tuning.recommend_plan |  |
| `V$SQLSTATS` | 있음, 열 다름 | tuning.recommend_candidates, tuning.recommend_literals |  |
| `V$STATNAME` | 있음(조건) | session.list, session.stats | 있음(V$STATNAME: STAT#,NAME,CLASS,CLASS_NAME). GV$STATNAME 은 없음 → V$STATNAME 사용 |
| `V$TRANSACTION` | 있음, 열 다름 | lock.idle_transactions, lock.list, session.my_blocking, session.transaction |  |
| `V$VERSION` | 있음, 열 다름 | dbinfo.instance, dbinfo.version, tuning.report_env |  |

### 3.3 그대로 있음

| 뷰 | 판정 | 카탈로그 id | 비고 |
|---|---|---|---|
| `*_COL_COMMENTS` | 있음 | meta.columns |  |
| `*_CONS_COLUMNS` | 있음 | app.fk_index_check, meta.constraint_columns, meta.constraints, meta.detail_referenced_by |  |
| `*_ERRORS` | 있음 | meta.compile_errors, meta.java_errors |  |
| `*_EXTERNAL_TABLES` | 있음 | dbinfo.column_reorder_traits |  |
| `*_IND_COLUMNS` | 있음 | app.fk_index_check, meta.index_columns, meta.indexes, tuning.index_columns |  |
| `*_IND_EXPRESSIONS` | 있음 | meta.index_columns |  |
| `*_INDEXES` | 있음 | meta.detail_size, meta.detail_size_user, meta.index_columns, meta.indexes, tuning.index_stats |  |
| `*_LOBS` | 있음 | meta.detail_size, meta.detail_size_user |  |
| `*_MVIEW_LOGS` | 있음 | meta.mview_log |  |
| `*_PART_TABLES` | 있음 | meta.detail_partitions |  |
| `*_SEGMENTS` | 있음 | meta.detail_size, meta.detail_size_user |  |
| `*_SOURCE` | 있음 | dbinfo.column_reorder_source, dbinfo.source_search, meta.source |  |
| `*_TAB_COLUMNS` | 있음 | dbinfo.column_search, tuning.column_stats |  |
| `*_TAB_COMMENTS` | 있음 | meta.table_comment, meta.table_comments |  |
| `*_TAB_HISTOGRAMS` | 있음 | tuning.histogram |  |
| `*_TRIGGERS` | 있음 | meta.detail_triggers |  |
| `*_USERS` | 있음 | meta.schemas |  |
| `V$ACCESS` | 있음 | meta.object_users |  |
| `V$DATAFILE` | 있음 | lock.locked_row |  |

## 4. 열 차이(뷰별)

«없는 열» 은 Oracle 카탈로그가 `alias.열` 로 쓰는데 Tibero 뷰에 없는 것이다. «대체» 는 Tibero 뷰의 열 목록에서 이름으로 짐작한 후보라 **뜻·단위는 T3 에서 값을 보고 확인**해야 한다.

| 뷰 | 없는 열 | 대체(후보) |
|---|---|---|
| `*_ARGUMENTS` | `DATA_LEVEL` | 열 없음 |
| `*_ARGUMENTS` | `DEFAULTED` | 열 없음 |
| `*_ARGUMENTS` | `SEQUENCE` | 열 없음 → POSITION |
| `*_CONSTRAINTS` | `SEARCH_CONDITION_VC` | 열 없음 → SEARCH_CONDITION |
| `*_DEPENDENCIES` | `REFERENCED_NAME` | PARENT_OBJ_NAME |
| `*_DEPENDENCIES` | `REFERENCED_OWNER` | PARENT_OBJ_OWNER |
| `*_DEPENDENCIES` | `REFERENCED_TYPE` | PARENT_OBJ_TYPE |
| `*_HIST_SNAPSHOT` | `DBID` | 열 없음(INSTANCE_NUMBER 만) |
| `*_HIST_SQL_PLAN` | `DBID` | 열 없음 |
| `*_HIST_SQL_PLAN` | `OPTIONS` | 열 없음 |
| `*_HIST_SQL_PLAN` | `SQL_ID` | 열 없음 → SQL_HASH_VALUE |
| `*_HIST_SQLSTAT` | `BUFFER_GETS_DELTA` | 열 없음(위와 같음) |
| `*_HIST_SQLSTAT` | `DBID` | 열 없음 |
| `*_HIST_SQLSTAT` | `ELAPSED_TIME_DELTA` | 열 없음(위와 같음) |
| `*_HIST_SQLSTAT` | `EXECUTIONS_DELTA` | 열 없음(EXECUTIONS 누적, 델타는 스냅샷 차이로 계산) |
| `*_HIST_SQLSTAT` | `SQL_ID` | 열 없음 → SQL_HASH_VALUE(해시 값으로 식별) |
| `*_JAVA_ARGUMENTS` | `BASE_TYPE` | ARGUMENT_TYPE |
| `*_JAVA_CLASSES` | `IS_STATIC` | 열 없음 |
| `*_JAVA_FIELDS` | `BASE_TYPE` | FIELD_TYPE |
| `*_JAVA_METHODS` | `BASE_TYPE` | RETURN_TYPE |
| `*_JAVA_METHODS` | `IS_ABSTRACT` | 열 없음 |
| `*_OBJECTS` | `DATA_OBJECT_ID` | 열 없음 → OBJECT_ID |
| `*_OBJECTS` | `EDITIONABLE` | 열 없음 |
| `*_PROCEDURES` | `OBJECT_TYPE` | 열 없음 |
| `*_PROCEDURES` | `OVERLOAD` | 열 없음 |
| `*_PROCEDURES` | `SUBPROGRAM_ID` | 열 없음 |
| `*_RECYCLEBIN` | `CAN_PURGE` | 열 없음 |
| `*_RECYCLEBIN` | `CAN_UNDROP` | 열 없음 |
| `*_SYNONYMS` | `DB_LINK` | 열 없음 |
| `*_SYNONYMS` | `TABLE_NAME` | ORG_OBJECT_NAME |
| `*_SYNONYMS` | `TABLE_OWNER` | ORG_OBJECT_OWNER |
| `*_TAB_COL_STATISTICS` | `HISTOGRAM` | 열 없음(NUM_BUCKETS 로 짐작) |
| `*_TAB_COL_STATISTICS` | `OWNER` | TABLE_OWNER |
| `*_TAB_COLS` | `DOMAIN_NAME` | 열 없음 |
| `*_TAB_COLS` | `IDENTITY_COLUMN` | 열 없음 |
| `*_TAB_COLS` | `USER_GENERATED` | 열 없음(HIDDEN_COLUMN·VIRTUAL_COLUMN 은 있음) |
| `*_TAB_PARTITIONS` | `HIGH_VALUE` | BOUND |
| `*_TAB_PARTITIONS` | `PARTITION_POSITION` | PARTITION_NO |
| `*_TAB_PARTITIONS` | `TABLE_OWNER` | OWNER |
| `*_TAB_PRIVS` | `TABLE_SCHEMA` | OWNER |
| `*_TABLES` | `CLUSTER_NAME` | 열 없음 |
| `V$ACTIVE_SESSION_HISTORY` | `EVENT` | WAIT_EVENT |
| `V$ACTIVE_SESSION_HISTORY` | `SESSION_STATE` | 열 없음 |
| `V$ACTIVE_SESSION_HISTORY` | `WAIT_CLASS` | 열 없음 |
| `V$DATABASE` | `CDB` | 열 없음 |
| `V$DATABASE` | `CREATED` | CREATE_DATE |
| `V$DATABASE` | `DATABASE_ROLE` | 열 없음 |
| `V$DATABASE` | `FLASHBACK_ON` | 열 없음 |
| `V$DATABASE` | `FORCE_LOGGING` | 열 없음 |
| `V$INSTANCE` | `ARCHIVER` | 열 없음 |
| `V$INSTANCE` | `DATABASE_STATUS` | 열 없음(STATUS 는 있음) |
| `V$INSTANCE` | `EDITION` | 열 없음 → V$LICENSE.EDITION |
| `V$INSTANCE` | `INSTANCE_ROLE` | 열 없음 |
| `V$INSTANCE` | `LOGINS` | 열 없음 |
| `V$INSTANCE` | `VERSION_FULL` | 열 없음 → VERSION |
| `V$LOCK` | `BLOCK` | 열 없음(V$BLOCKER_SESSION 으로 대신) |
| `V$LOCK` | `REQUEST` | REQUESTED |
| `V$LOCK` | `SID` | SESS_ID |
| `V$OPEN_CURSOR` | `CURSOR_TYPE` | 열 없음 |
| `V$OPEN_CURSOR` | `LAST_SQL_ACTIVE_TIME` | 열 없음(EXEC_TIME 이 가까움) |
| `V$PROCESS` | `ADDR` | 열 없음 → PID |
| `V$PROCESS` | `TRACEFILE` | 열 없음 |
| `V$SESSION` | `BLOCKING_INSTANCE` | 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID) |
| `V$SESSION` | `BLOCKING_SESSION` | 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION |
| `V$SESSION` | `EVENT` | WAIT_EVENT |
| `V$SESSION` | `FINAL_BLOCKING_INSTANCE` | 열 없음 |
| `V$SESSION` | `FINAL_BLOCKING_SESSION` | 열 없음(최종 차단자는 WAITER_SESSION 을 따라 올라가며 계산) |
| `V$SESSION` | `LAST_CALL_ET` | 열 없음 → SQL_ET(의미 확인 필요) |
| `V$SESSION` | `P1` | 열 없음 → V$SESSION_WAIT.ID1 |
| `V$SESSION` | `P2` | 열 없음 → V$SESSION_WAIT.ID2 |
| `V$SESSION` | `P3` | 열 없음 |
| `V$SESSION` | `PADDR` | 열 없음 → PID (V$PROCESS.PID) |
| `V$SESSION` | `PROGRAM` | PROG_NAME |
| `V$SESSION` | `ROW_WAIT_BLOCK#` | ROW_WAIT_BLOCK_NO |
| `V$SESSION` | `ROW_WAIT_FILE#` | ROW_WAIT_FILE_NO |
| `V$SESSION` | `ROW_WAIT_OBJ#` | ROW_WAIT_OBJ_ID |
| `V$SESSION` | `ROW_WAIT_ROW#` | ROW_WAIT_ROW_NO |
| `V$SESSION` | `TADDR` | 열 없음 → V$TRANSACTION.SESS_ID = V$SESSION.SID |
| `V$SESSION` | `WAIT_CLASS` | 열 없음 → V$EVENT_NAME.CLASS 를 WAIT_EVENT 이름으로 합침 |
| `V$SESSION` | `WAIT_TIME_MICRO` | 열 없음 → WAIT_TIME(단위 확인 필요) |
| `V$SESSION_LONGOPS` | `ELAPSED_SECONDS` | ELAPSED_TIME(단위 확인 필요) |
| `V$SESSION_LONGOPS` | `MESSAGE` | 열 없음 |
| `V$SESSION_LONGOPS` | `TIME_REMAINING` | 열 없음 |
| `V$SESSTAT` | `STATISTIC#` | STAT# |
| `V$SQL` | `EXACT_MATCHING_SIGNATURE` | 열 없음 |
| `V$SQL` | `FORCE_MATCHING_SIGNATURE` | 열 없음 |
| `V$SQL` | `IS_BIND_AWARE` | 열 없음 |
| `V$SQL` | `IS_BIND_SENSITIVE` | 열 없음 |
| `V$SQL` | `PARSING_SCHEMA_NAME` | 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인) |
| `V$SQL` | `SQL_FULLTEXT` | 열 없음 → SQL_TEXT(전체인지 확인 필요), 조각은 V$SQLTEXT(PIECE) |
| `V$SQL_BIND_CAPTURE` | `DATATYPE_STRING` | TYPE_NAME |
| `V$SQL_BIND_CAPTURE` | `LAST_CAPTURED` | TIME_CAPTURED |
| `V$SQL_BIND_CAPTURE` | `VALUE_STRING` | VALUE |
| `V$SQL_BIND_CAPTURE` | `WAS_CAPTURED` | 열 없음 |
| `V$SQL_PLAN` | `OPTIONS` | 열 없음(OPERATION 에 합쳐 있는지 확인 필요) |
| `V$SQLSTATS` | `FORCE_MATCHING_SIGNATURE` | 열 없음 |
| `V$SQLSTATS` | `VERSION_COUNT` | 열 없음 |
| `V$TRANSACTION` | `ADDR` | 열 없음 → SESS_ID |
| `V$TRANSACTION` | `START_DATE` | START_TIME |
| `V$TRANSACTION` | `STATUS` | STATE |
| `V$TRANSACTION` | `USED_UBLK` | USED_BLK |
| `V$TRANSACTION` | `USED_UREC` | USED_REC |
| `V$TRANSACTION` | `XIDSLOT` | SLOT |
| `V$TRANSACTION` | `XIDSQN` | WRAP |
| `V$TRANSACTION` | `XIDUSN` | USN |
| `V$VERSION` | `BANNER_FULL` | 열 없음. V$VERSION 은 NAME,VALUE 행(PRODUCT_MAJOR,PRODUCT_MINOR,BUILD_NUMBER,…) 형태이고 BANNER 열은 비어 있음 |

## 5. 패키지·함수·구문

### 5.1 패키지(PUBLIC 동의어 + SYS 패키지)

| 패키지 | 판정 |
|---|---|
| `DBMS_JAVA` | 있음 |
| `DBMS_METADATA` | 있음 |
| `DBMS_ROWID` | 있음 |
| `DBMS_SQL` | 있음 |
| `DBMS_SQLTUNE` | 있음 |
| `DBMS_XPLAN` | 있음 |
| `DBMS_STATS` | 있음 |
| `DBMS_SESSION` | 있음 |
| `DBMS_OUTPUT` | 있음 |
| `DBMS_LOCK` | 동의어만 있고 SYS 패키지는 없음 |
| `DBMS_UTILITY` | 있음 |
| `DBMS_APPLICATION_INFO` | 있음 |
| `DBMS_MONITOR` | 없음 |
| `DBMS_DEBUG` | 있음 |
| `DBMS_FLASHBACK` | 있음 |
| `DBMS_SPACE` | 있음 |
| `DBMS_LOB` | 있음 |
| `DBMS_RANDOM` | 있음 |
| `DBMS_CRYPTO` | 있음 |
| `UTL_FILE` | 있음 |
| `UTL_RAW` | 있음 |
| `UTL_HTTP` | 있음 |

### 5.2 프로시저·함수

`ALL_PROCEDURES`/`ALL_ARGUMENTS` 로 센 값이다. 0 이면 이름이 안 보인다(함수가 패키지 본문에만 있고 사전에 안 올라오는 경우도 있어 «없음» 확정은 아님). Oracle 호출 형태가 그대로 되는지는 따로 적었다.

| 호출 | 사전에서 보임 | 확인 |
|---|---|---|
| `DBMS_JAVA.LONGNAME` | procedures=1, arguments=2 | `SELECT DBMS_JAVA.LONGNAME('abc') FROM DUAL` 됨 |
| `DBMS_JAVA.SHORTNAME` | procedures=1, arguments=2 | 호출은 되나 빈 값을 돌려줌(Oracle 은 입력 그대로 또는 줄인 이름) |
| `DBMS_METADATA.GET_DDL` | procedures=1, arguments=7 | `GET_DDL('TABLE', 이름, 소유자)` 됨 |
| `DBMS_METADATA.GET_DEPENDENT_DDL` | procedures=0, arguments=0 | 사전에 안 보임 |
| `DBMS_METADATA.SESSION_TRANSFORM` | procedures=0, arguments=0 | 사전에 안 보임 → DDL 옵션(저장 속성·제약 분리 등)을 다르게 처리해야 함 |
| `DBMS_METADATA.SET_TRANSFORM_PARAM` | procedures=0, arguments=0 | 사전에 안 보임(위와 같음) |
| `DBMS_ROWID.ROWID_CREATE` | procedures=1, arguments=5 | `ROWID_CREATE(1,1,1,1,1)` → ODP-11029 인수 불일치(인수 형식이 다름) |
| `DBMS_SQL.OPEN_CURSOR` | procedures=1, arguments=1 |  |
| `DBMS_SQL.PARSE` | procedures=3, arguments=12 |  |
| `DBMS_SQL.NATIVE` | procedures=0, arguments=0 | 사전에 안 보임(Oracle 도 상수라 프로시저 아님) |
| `DBMS_SQL.IS_OPEN` | procedures=1, arguments=2 |  |
| `DBMS_SQL.CLOSE_CURSOR` | procedures=1, arguments=1 |  |
| `DBMS_SQLTUNE.REPORT_SQL_MONITOR` | procedures=1, arguments=6 | `sql_id => …, type => 'TEXT'` 호출 → ODP-11029 인수 불일치(이름 지정 인수가 다름) |
| `DBMS_XPLAN.DISPLAY` | procedures=0, arguments=0 | `TABLE(DBMS_XPLAN.DISPLAY(…))` 안 됨(ODP-8026). `PLAN_TABLE` 은 있음. Tibero 의 EXPLAIN PLAN 결과를 읽는 방법은 별도 확인 |
| `DBMS_XPLAN.DISPLAY_CURSOR` | procedures=1, arguments=4 | `TABLE(DBMS_XPLAN.DISPLAY_CURSOR(NULL, NULL, 'TYPICAL'))` 됨. 반환 열 이름은 `COLUMN_VALUE`(Oracle 은 `PLAN_TABLE_OUTPUT`) |
| `DBMS_STATS.GATHER_TABLE_STATS` | procedures=2, arguments=22 |  |
| `DBMS_STATS.GATHER_SCHEMA_STATS` | procedures=1, arguments=8 |  |

### 5.3 함수·구문

| 시험 | 결과 |
|---|---|
| `SELECT SYS_CONTEXT('USERENV','SESSION_USER') FROM DUAL` | 됨 |
| `SELECT SYS_CONTEXT('USERENV','CURRENT_SCHEMA') FROM DUAL` | 됨 |
| `SELECT SYS_CONTEXT('USERENV','SID') FROM DUAL` | 안 됨: ODP-11006:Invalid USERENV parameter. |
| `SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM DUAL` | 안 됨: ODP-11006:Invalid USERENV parameter. |
| `SELECT SYS_CONTEXT('USERENV','INSTANCE') FROM DUAL` | 됨 |
| `SELECT SYS_CONTEXT('USERENV','DB_NAME') FROM DUAL` | 됨 |
| `SELECT SYS_CONTEXT('USERENV','SERVICE_NAME') FROM DUAL` | 안 됨: ODP-11006:Invalid USERENV parameter. |
| `SELECT SYS_CONTEXT('USERENV','LANG') FROM DUAL` | 됨 |
| `SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) FROM DUAL` | 됨 |
| `SELECT SYS_GUID() FROM DUAL` | 됨 |
| `SELECT SYS_CONNECT_BY_PATH('a','/') FROM DUAL CONNECT BY LEVEL<2` | 됨 |
| `SELECT * FROM DUAL FETCH FIRST 1 ROWS ONLY` | 됨 |
| `SELECT 1 FROM DUAL OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY` | 됨 |
| `SELECT LISTAGG(1,',') WITHIN GROUP (ORDER BY 1) FROM DUAL` | 됨 |
| `SELECT REGEXP_LIKE('a','a') FROM DUAL` | 안 됨: ODP-8036:Specified procedure or function was not found. |
| `SELECT COALESCE(NULL,1) FROM DUAL` | 됨 |
| `SELECT CAST(NULL AS VARCHAR2(10)) FROM DUAL` | 됨 |
| `SELECT TRUNC(1.5) FROM DUAL` | 됨 |
| `SELECT JSON_VALUE('{"a":1}','$.a') FROM DUAL` | 안 됨: ODP-8036:Specified procedure or function was not found. |
| `SELECT ROWID FROM DUAL` | 안 됨: ODP-8026:Invalid identifier. |
- 추가 확인: `SYS_CONTEXT('USERENV','SESSIONID')` 와 `USERENV('SESSIONID')` 됨(숫자 감사 세션 번호). 현재 세션 SID 는 `SELECT SID FROM V$MYSTAT WHERE ROWNUM = 1`. `SYS_CONTEXT('USERENV','INSTANCE')` 는 0(인스턴스 번호). `SYS_GUID()` 는 바이트 배열로 읽힘(드라이버가 `RAW` → `sbyte[]`).
- `SYS_CONTEXT('USERENV','DB_UNIQUE_NAME')` 도 `ODP-11006`. `SYS_CONTEXT('USERENV','SESSIONID')` 는 됨.
- `SELECT ROWID FROM DUAL` 은 안 됨(DUAL 에 ROWID 가 없음).

## 6. 카탈로그 파일별 영향

«결과»: 그대로 파싱됨 = `@PFX@` 를 `dba`·`all` 로 푼 모든 형태가 Tibero 에서 파싱 성공(값의 뜻은 별도) / 차이 있음 / 문장 아님(ALTER·GRANT·DECLARE). 차이 칸은 자동 대조 결과이며 첫 파싱 오류 위치와 다를 수 있다.

| 파일 | id | 결과 | 쓰는 뷰·패키지 | 차이 |
|---|---|---|---|---|
| `app/ddl_locks_grant.sql` | `app.ddl_locks_grant` | 문장 아님 | *_DDL_LOCKS | (비 SELECT: GRANT) |
| `app/disconnect_session.sql` | `app.disconnect_session` | 문장 아님 |  | (비 SELECT: ALTER) |
| `app/fk_index_check.sql` | `app.fk_index_check` | 차이 있음 | *_CONSTRAINTS, *_CONS_COLUMNS, *_IND_COLUMNS | 자리표시자(`{…}`)가 있어 이 방법으로는 못 쟀음 — T3 에서 실제 값으로 확인 |
| `app/kill_session.sql` | `app.kill_session` | 문장 아님 |  | (비 SELECT: ALTER) |
| `app/locked_row_select.sql` | `app.locked_row_select` | 차이 있음 |  | 자리표시자(`{…}`)가 있어 이 방법으로는 못 쟀음 — T3 에서 실제 값으로 확인 |
| `app/statistics_level.sql` | `app.statistics_level` | 문장 아님 |  | (비 SELECT: ALTER) |
| `app/trace_event_off.sql` | `app.trace_event_off` | 문장 아님 |  | (비 SELECT: ALTER) |
| `app/trace_event_on.sql` | `app.trace_event_on` | 문장 아님 |  | (비 SELECT: ALTER) |
| `app/trace_parse.sql` | `app.trace_parse` | 문장 아님 | DBMS_SQL | (비 SELECT: DECLARE) |
| `dbinfo/column_reorder_source.sql` | `dbinfo.column_reorder_source` | 차이 있음 | *_DEPENDENCIES, *_SOURCE | *_dependencies.referenced_name → PARENT_OBJ_NAME; *_dependencies.referenced_owner → PARENT_OBJ_OWNER |
| `dbinfo/column_reorder_traits.sql` | `dbinfo.column_reorder_traits` | 차이 있음 | *_EXTERNAL_TABLES, *_TABLES | *_tables.cluster_name → 열 없음 |
| `dbinfo/column_search.11.sql` | `dbinfo.column_search` | 그대로 파싱됨 | *_OBJECTS, *_TAB_COLUMNS |  |
| `dbinfo/column_search.sql` | `dbinfo.column_search` | 그대로 파싱됨 | *_OBJECTS, *_TAB_COLUMNS |  |
| `dbinfo/database.11.sql` | `dbinfo.database` | 차이 있음 | V$DATABASE | v$database.created → CREATE_DATE; v$database.database_role → 열 없음; v$database.flashback_on → 열 없음; v$database.force_logging → 열 없음 |
| `dbinfo/database.sql` | `dbinfo.database` | 차이 있음 | V$DATABASE | v$database.cdb → 열 없음; v$database.created → CREATE_DATE; v$database.database_role → 열 없음; v$database.flashback_on → 열 없음; v$database.force_logging → 열 없음 |
| `dbinfo/flashback_ddl.sql` | `dbinfo.flashback_ddl` | 그대로 파싱됨 | *_OBJECTS |  |
| `dbinfo/flashback_env.sql` | `dbinfo.flashback_env` | 차이 있음 | V$PARAMETER | v$parameter 없음 |
| `dbinfo/flashback_versions.sql` | `dbinfo.flashback_versions` | 차이 있음 |  | 자리표시자(`{…}`)가 있어 이 방법으로는 못 쟀음 — T3 에서 실제 값으로 확인 |
| `dbinfo/instance.11.sql` | `dbinfo.instance` | 차이 있음 | V$INSTANCE, V$VERSION | v$instance.archiver → 열 없음; v$instance.database_status → 열 없음(STATUS 는 있음); v$instance.instance_role → 열 없음; v$instance.logins → 열 없음 |
| `dbinfo/instance.sql` | `dbinfo.instance` | 차이 있음 | V$INSTANCE | v$instance.archiver → 열 없음; v$instance.database_status → 열 없음(STATUS 는 있음); v$instance.edition → 열 없음 → V$LICENSE.EDITION; v$instance.instance_role → 열 없음; v$instance.logins → 열 없음; v$instance.version_full → 열 없음 → VERSION |
| `dbinfo/nls.sql` | `dbinfo.nls` | 그대로 파싱됨 |  |  |
| `dbinfo/object_search.11.sql` | `dbinfo.object_search` | 그대로 파싱됨 | *_OBJECTS |  |
| `dbinfo/object_search.sql` | `dbinfo.object_search` | 그대로 파싱됨 | *_OBJECTS |  |
| `dbinfo/options.sql` | `dbinfo.options` | 차이 있음 | V$OPTION | v$option 없음 |
| `dbinfo/parameters.sql` | `dbinfo.parameters` | 차이 있음 | V$PARAMETER | v$parameter 없음 |
| `dbinfo/recyclebin.sql` | `dbinfo.recyclebin` | 차이 있음 | *_RECYCLEBIN | *_recyclebin.can_purge → 열 없음; *_recyclebin.can_undrop → 열 없음 |
| `dbinfo/references.sql` | `dbinfo.references` | 차이 있음 | *_DEPENDENCIES | *_dependencies.referenced_name → PARENT_OBJ_NAME; *_dependencies.referenced_owner → PARENT_OBJ_OWNER; *_dependencies.referenced_type → PARENT_OBJ_TYPE |
| `dbinfo/regex_check.sql` | `dbinfo.regex_check` | 그대로 파싱됨 |  |  |
| `dbinfo/source_search.11.sql` | `dbinfo.source_search` | 그대로 파싱됨 | *_SOURCE |  |
| `dbinfo/source_search.sql` | `dbinfo.source_search` | 그대로 파싱됨 | *_SOURCE |  |
| `dbinfo/version.11.sql` | `dbinfo.version` | 그대로 파싱됨 | V$VERSION |  |
| `dbinfo/version.sql` | `dbinfo.version` | 차이 있음 | V$VERSION | v$version.banner_full → 열 없음. V$VERSION 은 NAME,VALUE 행(PRODUCT_MAJOR,PRODUCT_MINOR,BUILD_NUMBER,…) 형태이고 BANNER 열은 비어 있음 |
| `lock/blocker_impact.sql` | `lock.blocker_impact` | 차이 있음 | V$SESSION | v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.final_blocking_instance → 열 없음; v$session.final_blocking_session → 열 없음(최종 차단자는 WAITER_SESSION 을 따라 올라가며 계산); v$session.wait_time_micro → 열 없음 → WAIT_TIME(단위 확인 필요) |
| `lock/ddl_locks.sql` | `lock.ddl_locks` | 차이 있음 | *_DDL_LOCKS, V$SESSION | SYS.DBA_DDL_LOCKS 로 읽힘. SYS_CONTEXT('USERENV','INSTANCE') 는 됨. V$SESSION 열 차이 없음(SID·SERIAL#·TYPE 만 씀) |
| `lock/deadlocks.sql` | `lock.deadlocks` | 차이 있음 | V$DIAG_ALERT_EXT | v$diag_alert_ext 없음 |
| `lock/idle_transactions.sql` | `lock.idle_transactions` | 차이 있음 | V$LOCK, V$SESSION, V$TRANSACTION | v$lock.sid → SESS_ID; v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.last_call_et → 열 없음 → SQL_ET(의미 확인 필요); v$session.program → PROG_NAME; v$session.taddr → 열 없음 → V$TRANSACTION.SESS_ID = V$SESSION.SID; v$transaction.addr → 열 없음 → SESS_ID; v$transaction.start_date → START_TIME; v$transaction.used_ublk → USED_BLK |
| `lock/list.sql` | `lock.list` | 차이 있음 | *_OBJECTS, V$LOCK, V$SESSION, V$TRANSACTION | v$lock.block → 열 없음(V$BLOCKER_SESSION 으로 대신); v$lock.request → REQUESTED; v$lock.sid → SESS_ID; v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.event → WAIT_EVENT; v$session.p1 → 열 없음 → V$SESSION_WAIT.ID1; v$session.p2 → 열 없음 → V$SESSION_WAIT.ID2; v$session.p3 → 열 없음; v$session.program → PROG_NAME; v$session.wait_time_micro → 열 없음 → WAIT_TIME(단위 확인 필요); v$transaction.start_date → START_TIME; v$transaction.used_ublk → USED_BLK; v$transaction.xidslot → SLOT; v$transaction.xidsqn → WRAP; v$transaction.xidusn → USN |
| `lock/locked_row.sql` | `lock.locked_row` | 차이 있음 | *_OBJECTS, V$DATAFILE, V$SESSION, DBMS_ROWID | *_objects.data_object_id → 열 없음 → OBJECT_ID; v$session.event → WAIT_EVENT; v$session.row_wait_block# → ROW_WAIT_BLOCK_NO; v$session.row_wait_file# → ROW_WAIT_FILE_NO; v$session.row_wait_obj# → ROW_WAIT_OBJ_ID; v$session.row_wait_row# → ROW_WAIT_ROW_NO |
| `lock/waiting_binds.sql` | `lock.waiting_binds` | 차이 있음 | V$SESSION, V$SQL_BIND_CAPTURE | v$sql_bind_capture.datatype_string → TYPE_NAME; v$sql_bind_capture.last_captured → TIME_CAPTURED; v$sql_bind_capture.value_string → VALUE; v$sql_bind_capture.was_captured → 열 없음 |
| `meta/arguments.sql` | `meta.arguments` | 차이 있음 | *_ARGUMENTS | *_arguments.data_level → 열 없음; *_arguments.defaulted → 열 없음; *_arguments.sequence → 열 없음 → POSITION; DBA_ARGUMENTS 없음(ALL_ARGUMENTS 는 있음) |
| `meta/columns.11.sql` | `meta.columns` | 그대로 파싱됨 | *_COL_COMMENTS, *_TAB_COLS |  |
| `meta/columns.23.sql` | `meta.columns` | 차이 있음 | *_COL_COMMENTS, *_TAB_COLS | *_tab_cols.domain_name → 열 없음; *_tab_cols.identity_column → 열 없음; *_tab_cols.user_generated → 열 없음(HIDDEN_COLUMN·VIRTUAL_COLUMN 은 있음) |
| `meta/columns.sql` | `meta.columns` | 차이 있음 | *_COL_COMMENTS, *_TAB_COLS | *_tab_cols.identity_column → 열 없음; *_tab_cols.user_generated → 열 없음(HIDDEN_COLUMN·VIRTUAL_COLUMN 은 있음) |
| `meta/compile_errors.sql` | `meta.compile_errors` | 그대로 파싱됨 | *_ERRORS |  |
| `meta/constraint_columns.sql` | `meta.constraint_columns` | 그대로 파싱됨 | *_CONSTRAINTS, *_CONS_COLUMNS |  |
| `meta/constraints.11.sql` | `meta.constraints` | 그대로 파싱됨 | *_CONSTRAINTS, *_CONS_COLUMNS |  |
| `meta/constraints.sql` | `meta.constraints` | 차이 있음 | *_CONSTRAINTS, *_CONS_COLUMNS | *_constraints.search_condition_vc → 열 없음 → SEARCH_CONDITION |
| `meta/ddl.sql` | `meta.ddl` | 문장 아님 | DBMS_METADATA | (비 SELECT: DECLARE) |
| `meta/detail_dependencies.sql` | `meta.detail_dependencies` | 차이 있음 | *_DEPENDENCIES | *_dependencies.referenced_name → PARENT_OBJ_NAME; *_dependencies.referenced_owner → PARENT_OBJ_OWNER; *_dependencies.referenced_type → PARENT_OBJ_TYPE |
| `meta/detail_general.11.sql` | `meta.detail_general` | 그대로 파싱됨 | *_OBJECTS |  |
| `meta/detail_general.sql` | `meta.detail_general` | 차이 있음 | *_OBJECTS | *_objects.editionable → 열 없음 |
| `meta/detail_partitions.sql` | `meta.detail_partitions` | 차이 있음 | *_PART_TABLES, *_TAB_PARTITIONS | *_tab_partitions.high_value → BOUND; *_tab_partitions.partition_position → PARTITION_NO; *_tab_partitions.table_owner → OWNER |
| `meta/detail_privileges.sql` | `meta.detail_privileges` | 그대로 파싱됨 | *_TAB_PRIVS |  |
| `meta/detail_privileges_user.sql` | `meta.detail_privileges_user` | 차이 있음 | *_TAB_PRIVS | *_tab_privs.table_schema → OWNER |
| `meta/detail_referenced_by.sql` | `meta.detail_referenced_by` | 그대로 파싱됨 | *_CONSTRAINTS, *_CONS_COLUMNS |  |
| `meta/detail_size.sql` | `meta.detail_size` | 그대로 파싱됨 | *_INDEXES, *_LOBS, *_SEGMENTS |  |
| `meta/detail_size_user.sql` | `meta.detail_size_user` | 그대로 파싱됨 | *_INDEXES, *_LOBS, *_SEGMENTS |  |
| `meta/detail_stats.sql` | `meta.detail_stats` | 그대로 파싱됨 | *_TABLES |  |
| `meta/detail_triggers.sql` | `meta.detail_triggers` | 그대로 파싱됨 | *_TRIGGERS |  |
| `meta/index_columns.sql` | `meta.index_columns` | 그대로 파싱됨 | *_INDEXES, *_IND_COLUMNS, *_IND_EXPRESSIONS |  |
| `meta/indexes.sql` | `meta.indexes` | 그대로 파싱됨 | *_INDEXES, *_IND_COLUMNS |  |
| `meta/java_arguments.sql` | `meta.java_arguments` | 차이 있음 | *_JAVA_ARGUMENTS | *_java_arguments.base_type → ARGUMENT_TYPE |
| `meta/java_available.sql` | `meta.java_available` | 그대로 파싱됨 | *_OBJECTS |  |
| `meta/java_class.sql` | `meta.java_class` | 차이 있음 | *_JAVA_CLASSES | *_java_classes.is_static → 열 없음 |
| `meta/java_errors.sql` | `meta.java_errors` | 그대로 파싱됨 | *_ERRORS, DBMS_JAVA |  |
| `meta/java_fields.sql` | `meta.java_fields` | 차이 있음 | *_JAVA_FIELDS | *_java_fields.base_type → FIELD_TYPE |
| `meta/java_implements.sql` | `meta.java_implements` | 차이 있음 | *_JAVA_IMPLEMENTS | *_java_implements 없음 |
| `meta/java_inners.sql` | `meta.java_inners` | 차이 있음 | *_JAVA_INNERS | *_java_inners 없음 |
| `meta/java_longname.sql` | `meta.java_longname` | 그대로 파싱됨 | DBMS_JAVA |  |
| `meta/java_methods.sql` | `meta.java_methods` | 차이 있음 | *_JAVA_METHODS | *_java_methods.base_type → RETURN_TYPE; *_java_methods.is_abstract → 열 없음 |
| `meta/java_objects.sql` | `meta.java_objects` | 그대로 파싱됨 | *_OBJECTS |  |
| `meta/java_source_classes.sql` | `meta.java_source_classes` | 차이 있음 | *_JAVA_CLASSES, *_JAVA_DERIVATIONS | *_java_derivations 없음 |
| `meta/java_source_object.sql` | `meta.java_source_object` | 차이 있음 | *_JAVA_DERIVATIONS, *_OBJECTS, DBMS_JAVA | *_java_derivations 없음 |
| `meta/mview_log.sql` | `meta.mview_log` | 그대로 파싱됨 | *_MVIEW_LOGS |  |
| `meta/name_step.sql` | `meta.name_step` | 차이 있음 | *_OBJECTS, *_SYNONYMS | *_synonyms.db_link → 열 없음; *_synonyms.table_name → ORG_OBJECT_NAME; *_synonyms.table_owner → ORG_OBJECT_OWNER |
| `meta/object_users.sql` | `meta.object_users` | 그대로 파싱됨 | V$ACCESS |  |
| `meta/objects.11.sql` | `meta.objects` | 그대로 파싱됨 | *_OBJECTS |  |
| `meta/objects.sql` | `meta.objects` | 그대로 파싱됨 | *_OBJECTS |  |
| `meta/procedures.sql` | `meta.procedures` | 차이 있음 | *_PROCEDURES | *_procedures.object_type → 열 없음; *_procedures.overload → 열 없음; *_procedures.subprogram_id → 열 없음 |
| `meta/schemas.sql` | `meta.schemas` | 그대로 파싱됨 | *_USERS |  |
| `meta/source.sql` | `meta.source` | 그대로 파싱됨 | *_SOURCE |  |
| `meta/table_comment.sql` | `meta.table_comment` | 그대로 파싱됨 | *_TAB_COMMENTS |  |
| `meta/table_comments.sql` | `meta.table_comments` | 그대로 파싱됨 | *_TAB_COMMENTS |  |
| `plan/cursor_steps.sql` | `plan.cursor_steps` | 차이 있음 | V$SQL_PLAN_STATISTICS_ALL | v$sql_plan_statistics_all 없음 |
| `plan/cursor_text.sql` | `plan.cursor_text` | 차이 있음 | DBMS_XPLAN | TABLE(DBMS_XPLAN.DISPLAY_CURSOR(…)) 는 되지만 반환 열 이름이 PLAN_TABLE_OUTPUT 이 아니라 COLUMN_VALUE |
| `plan/explain_steps.sql` | `plan.explain_steps` | 그대로 파싱됨 |  |  |
| `plan/explain_text.sql` | `plan.explain_text` | 차이 있음 | DBMS_XPLAN | DBMS_XPLAN.DISPLAY 없음(PLAN_TABLE 은 있음). EXPLAIN PLAN 결과 읽는 법 별도 확인 |
| `plan/last_cursor_text.sql` | `plan.last_cursor_text` | 차이 있음 | DBMS_XPLAN | 위와 같음(열 이름 COLUMN_VALUE) |
| `plan/monitor_report.sql` | `plan.monitor_report` | 차이 있음 | DBMS_SQLTUNE | DBMS_SQLTUNE.REPORT_SQL_MONITOR 는 있으나 이름 지정 인수(sql_id => …, type => …)가 안 맞음 |
| `plan/monitor_report_html.sql` | `plan.monitor_report_html` | 차이 있음 | DBMS_SQLTUNE | 위와 같음 |
| `session/blocking_tree.sql` | `session.blocking_tree` | 차이 있음 | *_OBJECTS, V$SESSION | v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.event → WAIT_EVENT; v$session.final_blocking_instance → 열 없음; v$session.final_blocking_session → 열 없음(최종 차단자는 WAITER_SESSION 을 따라 올라가며 계산); v$session.row_wait_obj# → ROW_WAIT_OBJ_ID; v$session.wait_time_micro → 열 없음 → WAIT_TIME(단위 확인 필요) |
| `session/detail.sql` | `session.detail` | 차이 있음 | V$SESSION | v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.event → WAIT_EVENT; v$session.last_call_et → 열 없음 → SQL_ET(의미 확인 필요); v$session.program → PROG_NAME; v$session.wait_class → 열 없음 → V$EVENT_NAME.CLASS 를 WAIT_EVENT 이름으로 합침; v$session.wait_time_micro → 열 없음 → WAIT_TIME(단위 확인 필요) |
| `session/io.sql` | `session.io` | 차이 있음 | V$SESS_IO | GV$SESS_IO 없음 → V$SESSION_IO |
| `session/list.sql` | `session.list` | 차이 있음 | V$SESSION, V$SESSTAT, V$STATNAME | v$session.blocking_instance → 열 없음(위와 같음, GV$WAITER_SESSION 에 WAIT_INST_ID·HOLD_INST_ID); v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.event → WAIT_EVENT; v$session.last_call_et → 열 없음 → SQL_ET(의미 확인 필요); v$session.program → PROG_NAME; v$session.wait_class → 열 없음 → V$EVENT_NAME.CLASS 를 WAIT_EVENT 이름으로 합침; v$session.wait_time_micro → 열 없음 → WAIT_TIME(단위 확인 필요); v$sesstat.statistic# → STAT#; GV$STATNAME 없음 → V$STATNAME 사용 |
| `session/locks.sql` | `session.locks` | 차이 있음 | *_OBJECTS, V$LOCKED_OBJECT | v$locked_object 없음 |
| `session/longops.sql` | `session.longops` | 차이 있음 | V$SESSION_LONGOPS | v$session_longops.elapsed_seconds → ELAPSED_TIME(단위 확인 필요); v$session_longops.message → 열 없음; v$session_longops.time_remaining → 열 없음 |
| `session/my_blocking.sql` | `session.my_blocking` | 차이 있음 | V$PARAMETER, V$SESSION, V$TRANSACTION | v$session.blocking_session → 열 없음 → V$WAITER_SESSION(WAIT_SID,HOLD_SID,TYPE,ID1,ID2,MODE_REQ,MODE_HELD) / V$BLOCKER_SESSION; v$session.final_blocking_session → 열 없음(최종 차단자는 WAITER_SESSION 을 따라 올라가며 계산); v$session.last_call_et → 열 없음 → SQL_ET(의미 확인 필요); v$session.taddr → 열 없음 → V$TRANSACTION.SESS_ID = V$SESSION.SID; v$transaction.addr → 열 없음 → SESS_ID; v$transaction.start_date → START_TIME; v$parameter 없음 |
| `session/open_cursors.11.sql` | `session.open_cursors` | 차이 있음 | V$OPEN_CURSOR | v$open_cursor.cursor_type → 열 없음; v$open_cursor.last_sql_active_time → 열 없음(EXEC_TIME 이 가까움) |
| `session/open_cursors.sql` | `session.open_cursors` | 차이 있음 | V$OPEN_CURSOR | v$open_cursor.cursor_type → 열 없음; v$open_cursor.last_sql_active_time → 열 없음(EXEC_TIME 이 가까움) |
| `session/plan.sql` | `session.plan` | 차이 있음 | DBMS_XPLAN | 위와 같음(열 이름 COLUMN_VALUE) |
| `session/sql_text.11.sql` | `session.sql_text` | 차이 있음 | V$SQL | v$sql.sql_fulltext → 열 없음 → SQL_TEXT(전체인지 확인 필요), 조각은 V$SQLTEXT(PIECE) |
| `session/sql_text.sql` | `session.sql_text` | 차이 있음 | V$SQL | v$sql.sql_fulltext → 열 없음 → SQL_TEXT(전체인지 확인 필요), 조각은 V$SQLTEXT(PIECE) |
| `session/stats.sql` | `session.stats` | 차이 있음 | V$SESSTAT, V$STATNAME | v$sesstat.statistic# → STAT#; GV$STATNAME 없음 → V$STATNAME 사용 |
| `session/trace_file.sql` | `session.trace_file` | 차이 있음 | V$DIAG_INFO, V$PROCESS, V$SESSION | v$process.addr → 열 없음 → PID; v$process.tracefile → 열 없음; v$session.paddr → 열 없음 → PID (V$PROCESS.PID); v$diag_info 없음 |
| `session/transaction.sql` | `session.transaction` | 차이 있음 | V$SESSION, V$TRANSACTION | v$session.taddr → 열 없음 → V$TRANSACTION.SESS_ID = V$SESSION.SID; v$transaction.addr → 열 없음 → SESS_ID; v$transaction.status → STATE; v$transaction.used_ublk → USED_BLK; v$transaction.used_urec → USED_REC; v$transaction.xidslot → SLOT; v$transaction.xidsqn → WRAP; v$transaction.xidusn → USN |
| `session/wait_history.sql` | `session.wait_history` | 차이 있음 | V$SESSION_WAIT_HISTORY | v$session_wait_history 없음 |
| `sql/binds.sql` | `sql.binds` | 차이 있음 | V$SQL_BIND_CAPTURE | v$sql_bind_capture.datatype_string → TYPE_NAME; v$sql_bind_capture.last_captured → TIME_CAPTURED; v$sql_bind_capture.value_string → VALUE; v$sql_bind_capture.was_captured → 열 없음 |
| `sql/full_text.11.sql` | `sql.full_text` | 차이 있음 | V$SQL | v$sql.sql_fulltext → 열 없음 → SQL_TEXT(전체인지 확인 필요), 조각은 V$SQLTEXT(PIECE) |
| `sql/full_text.sql` | `sql.full_text` | 차이 있음 | V$SQL | v$sql.sql_fulltext → 열 없음 → SQL_TEXT(전체인지 확인 필요), 조각은 V$SQLTEXT(PIECE) |
| `sql/top.11.sql` | `sql.top` | 차이 있음 | V$SQL | v$sql.parsing_schema_name → 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인) |
| `sql/top.sql` | `sql.top` | 차이 있음 | V$SQL | v$sql.parsing_schema_name → 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인) |
| `tuning/ash_waits.sql` | `tuning.ash_waits` | 차이 있음 | V$ACTIVE_SESSION_HISTORY | v$active_session_history.event → WAIT_EVENT; v$active_session_history.session_state → 열 없음; v$active_session_history.wait_class → 열 없음 |
| `tuning/awr_plan_history.sql` | `tuning.awr_plan_history` | 차이 있음 | *_HIST_SNAPSHOT, *_HIST_SQLSTAT | *_hist_snapshot.dbid → 열 없음(INSTANCE_NUMBER 만); *_hist_sqlstat.buffer_gets_delta → 열 없음(위와 같음); *_hist_sqlstat.dbid → 열 없음; *_hist_sqlstat.elapsed_time_delta → 열 없음(위와 같음); *_hist_sqlstat.executions_delta → 열 없음(EXECUTIONS 누적, 델타는 스냅샷 차이로 계산); *_hist_sqlstat.sql_id → 열 없음 → SQL_HASH_VALUE(해시 값으로 식별) |
| `tuning/awr_plan_steps.sql` | `tuning.awr_plan_steps` | 차이 있음 | *_HIST_SQL_PLAN, V$DATABASE | *_hist_sql_plan.dbid → 열 없음; *_hist_sql_plan.options → 열 없음; *_hist_sql_plan.sql_id → 열 없음 → SQL_HASH_VALUE |
| `tuning/child_cursors.sql` | `tuning.child_cursors` | 차이 있음 | V$SQL | v$sql.is_bind_aware → 열 없음; v$sql.is_bind_sensitive → 열 없음 |
| `tuning/column_stats.sql` | `tuning.column_stats` | 차이 있음 | *_TAB_COLUMNS, *_TAB_COL_STATISTICS | *_tab_col_statistics.histogram → 열 없음(NUM_BUCKETS 로 짐작); *_tab_col_statistics.owner → TABLE_OWNER |
| `tuning/histogram.sql` | `tuning.histogram` | 그대로 파싱됨 | *_TAB_HISTOGRAMS |  |
| `tuning/index_columns.sql` | `tuning.index_columns` | 그대로 파싱됨 | *_IND_COLUMNS |  |
| `tuning/index_stats.sql` | `tuning.index_stats` | 차이 있음 | *_INDEXES, *_IND_STATISTICS | *_ind_statistics 없음 |
| `tuning/plan_controls.sql` | `tuning.plan_controls` | 차이 있음 | *_SQL_PATCHES, *_SQL_PLAN_BASELINES, *_SQL_PROFILES, V$SQL | v$sql.exact_matching_signature → 열 없음; v$sql.force_matching_signature → 열 없음; *_sql_patches 없음; *_sql_plan_baselines 없음; *_sql_profiles 없음 |
| `tuning/prev_cursor.sql` | `tuning.prev_cursor` | 차이 있음 | V$SESSION, V$SQL | SYS_CONTEXT('USERENV','SID') 불가 → SELECT SID FROM V$MYSTAT WHERE ROWNUM = 1 또는 SESSIONID. V$SESSION 쪽 열 차이는 4장 |
| `tuning/recommend_candidates.sql` | `tuning.recommend_candidates` | 차이 있음 | V$SQL, V$SQLSTATS | v$sql.parsing_schema_name → 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인); v$sqlstats.force_matching_signature → 열 없음; v$sqlstats.version_count → 열 없음 |
| `tuning/recommend_literals.sql` | `tuning.recommend_literals` | 차이 있음 | V$SQL, V$SQLSTATS | v$sql.parsing_schema_name → 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인); v$sqlstats.force_matching_signature → 열 없음 |
| `tuning/recommend_plan.sql` | `tuning.recommend_plan` | 차이 있음 | *_TABLES, V$SQL_PLAN | v$sql_plan.options → 열 없음(OPERATION 에 합쳐 있는지 확인 필요) |
| `tuning/report_env.sql` | `tuning.report_env` | 차이 있음 | V$PARAMETER, V$VERSION | v$parameter 없음 |
| `tuning/shared_cursor.sql` | `tuning.shared_cursor` | 차이 있음 | V$SQL_SHARED_CURSOR | v$sql_shared_cursor 없음 |
| `tuning/sql_stats.sql` | `tuning.sql_stats` | 차이 있음 | V$SQL | v$sql.parsing_schema_name → 열 없음 → PARSING_SCHEMA_ID(+ 사용자 사전과 조인) |
| `tuning/table_stats.sql` | `tuning.table_stats` | 차이 있음 | *_TAB_STATISTICS | *_tab_statistics 없음 |
| `tuning/trace_file.sql` | `tuning.trace_file` | 차이 있음 | V$PROCESS, V$SESSION | v$process.addr → 열 없음 → PID; v$process.tracefile → 열 없음; v$session.paddr → 열 없음 → PID (V$PROCESS.PID) |
| `tuning/trace_file_self.sql` | `tuning.trace_file_self` | 차이 있음 | V$DIAG_INFO | V$DIAG_INFO 없음 |
| `tuning/trace_lines.sql` | `tuning.trace_lines` | 차이 있음 | V$DIAG_TRACE_FILE_CONTENTS | v$diag_trace_file_contents 없음 |
| `tuning/wait_sample.sql` | `tuning.wait_sample` | 차이 있음 | V$SESSION | v$session.event → WAIT_EVENT; v$session.wait_class → 열 없음 → V$EVENT_NAME.CLASS 를 WAIT_EVENT 이름으로 합침 |

## 7. T3 에 넘기는 말

1. **그대로 파싱된 파일부터 Tibero 용으로 옮긴다**: `meta/objects`·`meta/indexes`·`meta/index_columns`·`meta/source`·`meta/compile_errors`·`meta/schemas`·`meta/table_comment(s)`·`meta/detail_stats`·`meta/detail_triggers`·`meta/detail_referenced_by`·`dbinfo/object_search`·`dbinfo/source_search`·`dbinfo/column_search` 등. 같은 문장을 `src/Oranda.Tibero/Catalog/` 에 두되 값의 뜻은 확인한다.
2. **세션·잠금 계열은 새로 쓴다**: Tibero `V$SESSION` 열(`WAIT_EVENT`, `PROG_NAME`, `SQL_ET`, `ROW_WAIT_*_NO`) 과 `V$WAITER_SESSION`·`V$BLOCKER_SESSION`·`V$LOCK`(SESS_ID)·`V$TRANSACTION`(SESS_ID) 조인으로. `V$LOCKED_OBJECT` 는 쓸 수 없다. `DBA_DDL_LOCKS` 는 `SYS.` 를 붙인다.
3. **통계·튜닝 계열은 Oracle 만 쓰는 기능**(SQL 프로필, SQL 패치, 베이스라인, 공유 커서 사유, bind-aware)은 Tibero 에서 끄고 이유를 보인다(A5 의 «비활성 + 이유» 부품). AWR 은 `DBA_HIST_*` 가 일부 있으나 `SQL_ID` 대신 `SQL_HASH_VALUE`, 델타 열 없음이라 별도 설계.
4. **버전 판정**: Tibero 7 에서 이 표가 바뀔 수 있다(서버 없음). 판정은 «뷰가 있는가» 로 하고 버전 숫자로 막지 않는다는 설계(tibero.md 머리말)대로, 파일 접미사(`.7.sql`)로 갈라 쓴다.
5. **확인이 남은 것**: ① DBA 계정으로 같은 측정(권한 때문에 숨은 뷰가 없는지) ② 값의 뜻·단위(`WAIT_TIME`, `ELAPSED_TIME`, `SQL_ET`, `V$SESSION.STATE`) ③ `FROM {table} VERSIONS BETWEEN …`(플래시백 버전 조회) 지원 ④ Tibero 의 `EXPLAIN PLAN` 결과를 읽는 방법(`DBMS_XPLAN.DISPLAY` 가 없음) ⑤ `DBMS_METADATA` 변환 옵션을 대신할 방법.

## 8. X3 결과 — Tibero 판 `meta`·`dbinfo` 카탈로그 (2026-11-09, 서버 6.2.15)

`src/Oranda.Tibero/Catalog/{meta,dbinfo}/` 에 Oracle 판과 같은 id 의 Tibero 판 42개(meta 27 = 기존 `schemas` 포함, dbinfo 15)를 두었다. 파일마다 `@PFX@` 를 dba/all 로 풀어 바인드를 NULL 로 실제 서버에서 실행해 열 이름·순서가 Oracle 판과 같은 것을 확인했다(행 값은 바인드가 NULL 이라 파일마다 별도 조회로 일부만 확인). 버전 변형(`.11`/`.23`)이 있는 id 는 Tibero 가 가장 보수적인 `.11` 쪽 문장을 기준으로 파일 하나(min-version 6)만 둔다.

**만들지 않은 id**
| id | 이유 | 대안 |
|---|---|---|
| `meta.java_*` 12개(`java_arguments`·`java_available`·`java_class`·`java_errors`·`java_fields`·`java_implements`·`java_inners`·`java_longname`·`java_methods`·`java_objects`·`java_source_classes`·`java_source_object`) | Java 저장 소스는 Tibero 첫 판 범위 밖(tibero.md 3장) | 없음(기능을 끈다) |
| `dbinfo.flashback_versions` | `VERSIONS BETWEEN` 구문을 Tibero 6 이 파싱하지 못함(`ODP-8022`), `VERSIONS_*` 의사 열 없음 | `AS OF TIMESTAMP`·`AS OF SCN` 시점 조회만(`flashback_env`·`flashback_ddl` 은 만들었다) |
| `dbinfo.options` | `V$OPTION` 없음 | 가장 가까운 `V$LICENSE`(에디션·제한)는 옵션이 아니라 만들지 않음. 호출하는 쪽의 「옵션」 구역은 쿼리 없음으로 처리 |

**Oracle 판과 달라 읽는 쪽(T3 C#)이 맞춰야 하는 것**
- `meta.ddl`: 바인드 `:type` → `:p_type` (Tibero PSM 에서 `type` 이 예약어). `GET_DDL` 은 TABLE 만 의미 있게 돈다(SEQUENCE·TRIGGER·VIEW·PROCEDURE·FUNCTION·SYNONYM = `ODP-14001`, INDEX·PACKAGE·TYPE = 빈 결과). `storage`·`segment_attributes`·`tablespace`·`dependents` 바인드는 받기만 하고 무시한다. 다른 형식의 DDL 은 사전 뷰(`*_source`·`*_triggers`·`*_indexes` 등)로 읽는 쪽이 만든다.
- `meta.arguments`: `dba_arguments` 가 없어 인자는 `all_arguments` 만 쓴다(`@PFX@` 없음). `data_level` 거르기 없음, `defaulted` 는 NULL(기본값 여부를 알 수 없음). Tibero `all_arguments.overload` 는 오버로드 번호가 아니라 멤버 순번(= `*_procedures.member_no`, 서브프로그램마다 고유)이라서, `@PFX@_procedures` 로 만든 순번 표(`m`)와 조인해 `meta.procedures` 와 같은 규칙(같은 이름이 둘 이상이면 선언 순서 `'1'..'n'`, 하나면 NULL)으로 `overload` 를 다시 낸다. 맨 끝에 열 `subprogram_id`(= 멤버 순번)를 더했다 — Oracle 판에는 없는 열이고, 읽는 쪽은 열 번호 0~9 를 Oracle 판과 같게 읽는다. 인자 없는 서브프로그램은 `all_arguments` 에 행이 없다.
- `meta.objects_by_prefix`(Oracle 판 없음, Tibero 만): 이름이 `:pattern` 으로 시작하는 객체를 모든 스키마에서 이름순(`NLS_SORT=BINARY` 가정, 6.2.15 기본값)으로 `:max_rows` 개까지. `meta.objects` 와 같은 객체 집합(파티션·LOB·Java·`BIN$` 제외). 읽는 쪽이 접두어의 `\`·`%`·`_` 를 `\` 로 이스케이프하고 끝에 `%` 를 붙여 `:pattern` 으로 넘긴다. 대소문자를 구분한다.
- `meta.columns`: `hidden_column`/`virtual_column` 을 `YES`/`NO` 로 바꿔 돌려주고 `column_id` 는 1부터로 맞춤. `identity_flag`·`domain_name` NULL. 문자 길이 판정에 `VARCHAR`/`NVARCHAR` 포함. 숫자 아닌 열의 precision/scale 은 NULL.
- `meta.constraint_columns`·`meta.constraints`: Tibero `*_cons_columns.position` 이 0부터라서 SQL 이 `+ 1` 해 1부터로 낸다(`*_ind_columns.column_position` 은 처음부터 1부터). 검사 조건 열은 `search_condition`.
- `meta.procedures`: `overload` 는 같은 소유자·패키지·이름이 둘 이상일 때만 선언 순서(`member_no`)대로 `'1'..'n'`, 하나면 NULL(`meta.arguments` 와 같은 규칙), `subprogram_id` = `member_no`, `object_type` 은 `*_objects` 조인. `meta.name_step`: `db_link` NULL(원격 시노님 따라가기 불가). `meta.object_users`: 내 SID 를 `V$MYSTAT` 로. `meta.detail_general`: `edition_flag` NULL.
- `dbinfo.column_search`: `data_type` 이 `VARCHAR`·`NVARCHAR` 로 나온다(Oracle `VARCHAR2`) — Tibero 용 `TypeText` 가 `VARCHAR` 계열을 문자 열로 인식해야 길이가 붙는다.
- `dbinfo.version`: `V$VERSION` 에 `BANNER` 가 없어 첫 줄 요약 + `NAME = VALUE` 줄들을 `banner` 한 열로 돌려준다. `dbinfo.database`: `database_role`·`force_logging`·`flashback_on` NULL. `dbinfo.instance`: `database_status`·`instance_role`·`logins`·`archiver`·`db_unique_name`·`service_name` NULL, `version` 은 주 버전 숫자. `dbinfo.parameters`: `V$PARAMETERS`, `isdefault` 는 `DECODE(value, dflt_value, …)`, 나머지 NULL. `dbinfo.recyclebin`: `can_undrop`·`can_purge` NULL(되돌리기·비우기 막힘), `droptime` 형식 미확인(서버에 행 없음). `dbinfo.column_reorder_traits`: `cluster_name` NULL.
