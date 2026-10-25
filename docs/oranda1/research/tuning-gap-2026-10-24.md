# Oranda SQL 튜닝 기능 조사와 타 도구 비교

작성일 2026-10-24. 저장소 파일은 수정하지 않았다. 웹 조사는 검색 결과 요약에 기댄 것이라, 도구별 세부는 「확인 못 함」으로 표시했다.

## 1. 요약

- Oranda 는 읽기 전용 경로의 튜닝 기초가 이미 탄탄하다. 예상 계획(EXPLAIN PLAN), 실제 계획(gv$sql_plan_statistics_all, DISPLAY_CURSOR ALLSTATS LAST), 단계별 비용·시간 비중 막대, 위험 단계 강조, 두 계획 비교, Top SQL(gv$sql), 바인드 캡처, 세션 트레이스 on/off, 문장별 v$mystat 차이가 있다.
- 없는 것: 튜닝 어드바이저류(SQL 재작성·힌트 대안 생성·인덱스 권고), 트레이스 파일 분석(TKPROF), 컬럼 통계·히스토그램·stale 화면, 계획 이력(AWR/v$sql_plan 시계열) 비교, 대기 이벤트 분해(ASH), SPM·SQL Profile 보기, 변형 SQL 일괄 벤치마크, 자식 커서 불일치(v$sql_shared_cursor)·ACS 정보.
- SQL 모니터 보고서(DBMS_SQLTUNE.REPORT_SQL_MONITOR)는 서비스 메서드와 카탈로그 SQL 은 있으나 화면 호출부를 못 찾았다(grep 으로 Oracle/Plans 밖 사용 없음). 「일부」로 본다.
- 경쟁 도구 가운데 Toad(SQL Optimizer)가 자동 재작성·대안 테스트·인덱스 최적화에서 가장 앞서고, SQL Developer 와 OEM 은 어드바이저·SQL Monitor·SPM 이 강하다. 단 대부분이 Tuning/Diagnostics Pack 이거나 DB 쓰기(PLAN_TABLE, 튜닝 태스크)를 동반한다. Oranda 는 이미 "팩 없는 읽기 전용" 노선이므로, 그 안에서 채울 수 있는 것을 먼저 채우는 것이 맞다.
- 우선 제안(P0): 실제 계획에서 gather_plan_statistics 자동 부여 또는 STATISTICS_LEVEL 안내, 컬럼 통계/히스토그램/stale 보기, 자식 커서 비교(바인드 민감도·ACS·shared_cursor 사유). 모두 SELECT 만으로 되고 팩이 필요 없다.

## 2. Oranda 현재 기능 (저장소 근거)

| 기능 | 상태 | 근거 |
|---|---|---|
| 예상 계획 | 있음 | OracleExecutionPlanService.cs 가 `EXPLAIN PLAN SET STATEMENT_ID` 를 정책 관문으로 전송, Catalog/plan/explain_steps.sql·explain_text.sql 로 읽음. PRD PLAN-01 |
| PLAN_TABLE 정리 | 안 함 | 같은 파일 주석: PLAN_TABLE 행은 지우지 않는다(접근 통제 도구가 DELETE 를 DML 로 봄). 단 EXPLAIN PLAN 자체는 PLAN_TABLE INSERT 라 DB 쓰기다. 읽기 전용 접속은 ORA-01456 안내 |
| 실제 계획 | 있음 | Catalog/plan/cursor_steps.sql(gv$sql_plan_statistics_all, last_output_rows/last_starts/last_elapsed_time), cursor_text.sql, session/plan.sql. 단축키 Ctrl+Shift+E |
| gather_plan_statistics 자동 부여 | 없음 | 힌트는 SnippetLibrary.cs 에 설명·삽입 조각으로만 있음. 계획 서비스에는 주입 코드 없음. STATISTICS_LEVEL 은 docs/wbs.md 에 사용자 사례만 있고 코드 처리 없음 |
| 트리 표시 | 있음 | PlanView.axaml: Id·순서·연산·객체·위험·비용·비용 비중·E-Rows·A-Rows·Starts·시간·실제 비중 열. PlanMetrics.cs, PlanTree.cs |
| 그래프(노드 다이어그램) | 없음 | 트리/텍스트/비교 3개 탭만 있음 |
| predicate | 있음 | cursor_steps.sql 이 access/filter_predicates 조회. 트리 표시는 PRD PLAN-03 |
| 형식 옵션 | 있음 | PlanViewModel.cs: BASIC/TYPICAL/SERIAL/ALL/ALLSTATS, LAST, +PEEKED_BINDS, +ALIAS, +OUTLINE (PlanView.axaml 체크박스). PROJECTION 등 나머지 항목 UI 는 PRD PLAN-05 에 있으나 체크박스는 4개만 확인 |
| 위험 단계 강조 | 있음 | Full Table Scan·Index Full Scan·Cartesian (BuildSteps, PRD PLAN-03) |
| 두 계획 비교 | 있음(고정한 것 vs 현재) | PlanComparer.cs(LCS 정렬, 같음/바뀜/추가/빠짐), PlanCompareRowItem.cs, 명령 oranda.plan.pinForCompare / compare |
| 계획 이력 비교(AWR·과거 child) | 없음 | dba_hist 사용 코드 없음(docs 에만 언급). 자식 커서 여럿이면 첫 자식만 사용(cursor_steps.sql 주석) |
| SQL 모니터 보고서 | 일부 | plan/monitor_report.sql(license: tuning)과 GetSqlMonitorReportAsync 가 있고 보유 선언·서버 설정 확인 후 전송. 화면 연결은 확인 못 함 |
| Top SQL | 있음 | Catalog/sql/top.sql(gv$sql, 정렬 6종, 필터), OracleMonitorService.TopSql.cs, SqlMonitorView(검색·실제 계획·전문·바인드 탭). 팩 불필요 |
| 바인드 캡처 | 있음 | Catalog/sql/binds.sql(gv$sql_bind_capture) |
| 바인드 민감/ACS(is_bind_sensitive 등) | 없음 | grep 0건 |
| 실행 통계(autotrace 유사) | 있음 | SqlPlus/SessionStatistics.cs: v$mystat 전후 차이(logical reads, physical reads, redo size, parse count, sorts, roundtrips). 계획 없는 autotrace statistics 에 해당 |
| 세션 트레이스 | 일부 | SessionMonitor: DBMS_MONITOR 로 켜기/끄기(waits/binds), 파일 경로(session/trace_file.sql). 파일 내용 읽기·TKPROF 없음. 켜기는 ALTER/패키지 호출이라 DB 상태 변경(INSERT 아님) |
| 10053 | 없음 | grep 0건(생성 코드 기준) |
| 세션 대기 | 일부 | session/wait_history.sql, 세션 목록의 event·wait_class. SQL 별 대기 분해(ASH)는 없음 |
| ASH/AWR | 없음 | 코드 0건. PRD SQLM-03 (P2, 이후), SAFE-07 보호장치 선행 |
| 테이블 통계 | 일부 | meta/detail_stats.sql: num_rows, blocks, avg_row_len, last_analyzed, sample_size만 |
| 컬럼 통계·히스토그램·stale | 없음 | dba_tab_col_statistics, dba_tab_statistics.stale_stats 사용 코드 0건 |
| 인덱스 권고 | 없음 | |
| SQL 재작성·힌트 대안 | 없음 | 힌트는 자동완성 조각만 |
| 계획 고정/SQL Profile/Baseline/Patch | 없음 | 보기도 쓰기도 없음 |
| 변형 SQL 일괄 벤치마크 | 없음 | |
| 계획 분석 묶음 저장 | 없음 | PRD PLAN-06 (P2, 이후) |
| 라이선스 보호 | 있음 | 카탈로그 `-- license: tuning` 머리 주석, PRD SAFE-07·C-06 |

## 3. 비교표

✓ 있음, △ 일부/조건부, ✗ 없음, ? 확인 못 함. 도구 열은 웹 검색 결과와 일반 지식을 합친 것이며 근거가 약한 칸은 「?」.

| 기능 | Oranda | Toad | SQL Developer | PL/SQL Developer | DBeaver | DataGrip | Orange | OEM |
|---|---|---|---|---|---|---|---|---|
| 예상 계획 트리/표 | ✓ (PlanView) | ✓ | ✓ | ✓ | ✓ | △(Oracle 지원 확인 못 함) | ✓(Plan Tool, 공개 매뉴얼) | ✓ |
| 계획 그래프 | ✗ | ✓ | △ | ✗(?) | △(그래프는 유료 에디션) | △(다이어그램, Oracle 확인 못 함) | ? | ✓ |
| cost/cardinality 표시 | ✓ | ✓ | ✓ | ✓ | ✓ | △ | ✓ | ✓ |
| predicate | ✓ | ✓ | ✓ | ✓ | △ | ? | ✓ | ✓ |
| projection/outline/alias 옵션 | △(outline·alias·peeked binds) | △(?) | △(?) | ✗(?) | ✗ | ✗ | ? | △ |
| 힌트 보기/삽입 | △(자동완성 조각) | ✓(대안 자동 힌트) | ✗ | ✗ | ✗ | ✗ | ? | △ |
| 실제 계획 ALLSTATS LAST | ✓(전제 설정 안내 없음) | ✓ | ✓(Autotrace 로 단계별 통계) | △(gather_plan_statistics 지원 요청 스레드 있음, 현재 상태 확인 못 함) | ✗ | ✗ | ? | ✓ |
| Autotrace/실행 통계 | ✓(v$mystat 차이) | ✓ | ✓ | ✓(Statistics 페이지) | ✗ | ✗ | ✓(문서상 Show Trace/통계, 확인 못 함) | ✓ |
| 계획 비교(두 개) | ✓(고정 vs 현재) | ✓(Xpert 다중 비교) | ✓(Autotrace 비교, 문서 확인 못 함) | ✗ | ✗ | ✗ | ? | ✓ |
| 계획 이력(AWR) | ✗ | △ | △(Diagnostics 필요) | ✗ | ✗ | ✗ | △(?) | ✓ |
| SQL Monitor 보고서 | △(서비스만) | △(?) | ✓(Tools > Monitor SQL) | ✗ | ✗ | ✗ | ✓(SQL Monitor, 팩 아님 v$ 기반일 수 있음, 확인 못 함) | ✓ |
| 10046 켜기 + TKPROF/트레이스 분석 | △(켜기만) | ✓(TKProf Wizard, Trace File Browser) | △(?) | ✓(SQL Trace) | ✗ | ✗ | ✓(Trace Tool) | △ |
| 10053 | ✗ | ✗(?) | ✗ | ✗ | ✗ | ✗ | ✗(?) | ✗ |
| SQL 재작성/대안 생성 | ✗ | ✓(Optimizer, Auto Optimize) | ✗ | ✗ | ✗ | ✗ | △(?) | △(Tuning Advisor 권고) |
| 힌트 주입 대안 | ✗ | ✓ | ✗ | ✗ | ✗ | ✗ | ? | △(Profile) |
| 인덱스 권고 | ✗ | ✓(Index Optimization) | △(Tuning Advisor) | ✗ | ✗ | ✗ | ? | ✓(SQL Access Advisor) |
| 테이블/컬럼 통계·히스토그램 | △(테이블만) | ✓ | ✓ | ✓ | △ | △ | ✓(Plan Tool 스키마 정보 탭) | ✓ |
| stale 통계 | ✗ | △ | △ | △ | ✗ | ✗ | ? | ✓ |
| 바인드 peek/ACS | △(peeked binds, 캡처값) | △(?) | ✗ | ✗ | ✗ | ✗ | ? | △ |
| Baseline/Profile/Patch | ✗ | △(Manage Plans) | △(SQL Plan Baselines 튜토리얼) | ✗ | ✗ | ✗ | ? | ✓ |
| SQL Tuning Advisor | ✗ | △ | ✓(Tune SQL) | ✗ | ✗ | ✗ | ? | ✓ |
| SQL Performance Analyzer | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ |
| SQL 별 대기 이벤트 | ✗ | △ | △ | △ | ✗ | ✗ | ✓(Wait Event Monitor) | ✓ |
| Top SQL | ✓(gv$sql) | ✓ | ✓ | ✗ | ✗ | ✗ | ✓(SQL Monitor) | ✓ |
| ASH/AWR | ✗ | △ | ✓(Diagnostics) | ✗ | ✗ | ✗ | ✓(ASH Monitor, AWR Manager) | ✓ |
| 변형 SQL 일괄 벤치마크 | ✗ | ✓(Optimizer 대안 테스트, Batch Optimize) | ✗ | ✗ | ✗ | ✗ | ✗(?) | ✓(SPA) |
| 쿼리 프로파일러(세션 통계 비교) | △ | △ | ✗ | △ | ✗ | ✗ | ✗ | ✗ |

dbForge Studio 는 열을 두지 않고 따로 적는다: Oracle 용 Query Profiler(EXPLAIN 기반, 세션 통계, 두 실행의 프로파일 비교, 이력 저장), PL/SQL 프로파일링, 시각 EXPLAIN. 상세 UI 는 확인 못 함.

## 4. Oranda 에 없는 튜닝 기능 (우선순위 제안)

쓰기 열의 의미: 없음 = SELECT·DISPLAY_CURSOR 만. 세션 = ALTER SESSION 같은 세션 상태 변경(INSERT/UPDATE/DELETE 아님, 접근 통제 도구가 막을지는 확인 못 함). 쓰기 = DML/DDL/태스크 생성.

| 우선 | 기능 | DB 쓰기 | 필요 라이선스 | Oracle 구현 한 줄 |
|---|---|---|---|---|
| P0 | 실제 계획 전제 보정(gather_plan_statistics 힌트 자동 부여 또는 STATISTICS_LEVEL=ALL 안내, A-Rows 가 비면 경고) | 세션(ALTER SESSION) 또는 없음(힌트 방식은 문장 수정) | 없음 | 실행 전 `/*+ GATHER_PLAN_STATISTICS */` 를 사본에 주입하거나, last_output_rows 가 null 일 때 안내 배너 표시 |
| P0 | 컬럼 통계·히스토그램·stale 화면 | 없음 | 없음 | `dba_tab_col_statistics`(num_distinct, histogram, num_buckets, density), `dba_tab_statistics.stale_stats`, `dba_tab_histograms` 조회 |
| P0 | 자식 커서 비교와 불일치 사유(바인드 민감·ACS·optimizer env) | 없음 | 없음 | `gv$sql`(is_bind_sensitive, is_bind_aware, is_shareable, plan_hash_value), `gv$sql_shared_cursor` 불일치 컬럼, child 별 plan 선택 UI |
| P1 | 자식 커서 모두 보기·현재 캐시된 과거 계획 간 비교 | 없음 | 없음 | cursor_steps.sql 의 child 필터 제거 후 child 별 plan_hash_value 로 PlanComparer 재사용 |
| P1 | 계획 그래프/플레임 시각화 | 없음 | 없음 | 기존 PlanStep 트리를 노드·간선 캔버스로 렌더링 |
| P1 | SQL 모니터 보고서 화면 연결(활성/텍스트) | 없음 | Tuning(+Diagnostics) | 기존 GetSqlMonitorReportAsync 를 SqlMonitor 탭에 연결, SAFE-07 통과 때만 활성 |
| P1 | 트레이스 파일 보기와 TKPROF 형식 요약 | 없음(읽기). 트레이스 켜기는 세션 변경 | 없음 | 11g 이후 `v$diag_info`·`v$diag_trace_file_contents`(12.2+) 로 파일 본문 읽기 후 클라이언트에서 TKPROF 식 집계. 11.2 는 서버 파일 접근 방식 필요(확인 못 함) |
| P1 | SQL 별 대기 이벤트 분해 | 없음 | 없음(v$ 뷰) / Diagnostics(ASH 이력) | `gv$session_event`, `v$sql_monitor` 없이 `gv$active_session_history` 는 팩 필요 → 팩 없는 대안은 샘플링 폴링으로 gv$session 의 event/sql_id 집계 |
| P1 | 인덱스 후보 제안(규칙 기반 휴리스틱) | 없음 | 없음 | 계획의 TABLE ACCESS FULL 의 filter predicate 컬럼 + 기존 `dba_ind_columns` 대조해 후보 문장 제시 |
| P1 | 변형 SQL 일괄 벤치마크(여러 SQL 을 N 회 실행해 시간·gets 표) | 없음(SELECT 한정) | 없음 | 문장별 v$mystat 전후 차이(SessionStatistics)를 재사용, 계획 해시도 함께 기록 |
| P2 | 계획 이력 비교(AWR) | 없음 | Diagnostics | `DBMS_XPLAN.DISPLAY_AWR`, `dba_hist_sql_plan`, `dba_hist_sqlstat` — SAFE-07 뒤에서만 |
| P2 | ASH 기반 SQL 대기 분해 | 없음 | Diagnostics | `gv$active_session_history` 를 sql_id 로 집계 |
| P2 | SQL 재작성·힌트 대안 자동 생성 | 없음(생성만) | 없음 | 서브쿼리 풀기·IN/EXISTS 변환 등 규칙 기반 + 힌트 후보(LEADING, USE_NL, INDEX)를 만들어 벤치마크로 검증. Toad 같은 정교함은 비현실적 |
| P2 | 10053 옵티마이저 트레이스 | 세션 | 없음 | `ALTER SESSION SET EVENTS '10053 trace name context forever'` 후 파일 읽기. 접근 통제 도구 영향 확인 못 함 |
| P2 | SQL Tuning Advisor | 쓰기(CREATE_TUNING_TASK, 태스크 데이터 저장) | Tuning + Diagnostics | `DBMS_SQLTUNE.CREATE_TUNING_TASK` / `REPORT_TUNING_TASK`. 승인 도구 영향 때문에 기본 제공 비권장 |
| P2 | SQL Profile/Baseline/Patch 보기 | 없음(보기) / 쓰기(적용) | 보기는 `dba_sql_profiles`·`dba_sql_plan_baselines`·`dba_sql_patches` 는 팩 표기 불명확(확인 못 함), 적용은 Tuning | 보기만 먼저 제공. 적용(LOAD_PLANS_FROM_CURSOR_CACHE 등)은 DB 쓰기라 제외 |
| P2 | 통계 수집(DBMS_STATS) | 쓰기 | 없음 | 실행 스크립트 제안만 하고 실행은 사용자 몫 |
| P2 | SQL Performance Analyzer | 쓰기 | Real Application Testing | 비권장 |
| P2 | 계획 분석 묶음 저장 | 없음 | 없음 | PRD PLAN-06 |

### 읽기 전용 대안 요약

- 예상 계획 대신: 이미 실행된 SQL 이면 `DBMS_XPLAN.DISPLAY_CURSOR(sql_id, child, 'ALLSTATS LAST')`(Oranda 가 이미 사용). EXPLAIN PLAN 은 PLAN_TABLE INSERT 이므로 승인 대상이 될 수 있다는 점을 UI 에 알린다.
- 튜닝 어드바이저 대신: 통계·히스토그램·자식 커서·바인드·계획 단계 시간 비중을 한 화면에 모아 수동 진단을 돕는다.
- ASH 대신: 팩 없이 gv$session 샘플링.

## 5. 출처 URL

- Toad SQL Optimizer 10.1 사용자 가이드: https://support.quest.com/technical-documents/sql-optimizer-for-oracle/10.1/user-guide , https://support.quest.com/technical-documents/sql-optimizer-for-oracle/10.1/user-guide/37 , https://support.quest.com/technical-documents/sql-optimizer-for-oracle/10.1/user-guide/57
- Toad Auto Optimize 블로그: https://blog.quest.com/product-post/quick-and-easy-sql-optimization-with-toad-for-oracle/
- Toad 2025 R2 Getting Started(Explain Plan): https://support.quest.com/technical-documents/toad-for-oracle/2025%20r2/getting-started-guide/42
- Toad TKProf: https://www.oreilly.com/library/view/toad-for-oracle/9780134131900/ch10lev2sec1.html , https://forums.toadworld.com/t/how-to-use-tkprof-wizard/29809 , https://support.quest.com/toad-for-oracle/kb/334252
- Toad Xpert 설명: https://services.pitt.edu/TDClient/33/Portal/KB/PrintArticle?ID=381
- Database Journal(힌트 위주 대안 평): https://www.databasejournal.com/oracle/getting-inside-the-optimizer/
- Oracle SQL Developer 관련: https://blogs.oracle.com/sql/post/how-to-create-an-execution-plan , https://docs.oracle.com/en/database/oracle/developer-tools-for-vs/23.8/online-help/performance_tuning_overview.html (Visual Studio 도구 도움말이며 SQL Developer 직접 문서 아님), https://www.oracle.com/webfolder/technetwork/tutorials/obe/db/sqldev/r40/Tuning_Utilities_OBE/tuning_utilities.html , https://support.oracle.com/knowledge/Middleware/2165534_1.html
- PL/SQL Developer: https://forums.allroundautomations.com/threads/support-gather_plan_statistics.12654 , https://forums.allroundautomations.com/threads/explain-plan-tab.3994/ , https://www.allroundautomations.com/?p=1838 , https://origin2.componentsource.com/sites/default/files/resources/allround-automations/533856/manual.pdf
- DBeaver: https://dbeaver.com/docs/dbeaver/26.0/Query-Execution-Plan/
- DataGrip: https://www.jetbrains.com/help/datagrip/2025.2/query-execution-plan.html
- dbForge Studio for Oracle: https://www.devart.com/dbforge/oracle/studio/oracle-sql-profiler.html , https://www.devart.com/dbforge/oracle/studio/performance-tuning.html
- OEM 및 라이선스: https://docs.oracle.com/en/enterprise-manager/cloud-control/enterprise-manager-cloud-control/24.1/oemli/enterprise-database-management.html , https://docs.oracle.com/en/enterprise-manager/cloud-control/enterprise-manager-cloud-control/13.5/emadb/monitoring-and-administration-tasks.html , https://docs.oracle.com/en/database/oracle/oracle-database/18/tgsql/managing-sql-plan-baselines.html , https://docs.oracle.com/database/122/RATUG/comparing-sql-trials.htm , https://redresscompliance.com/oracle-tuning-pack-licensing (제3자, 공식 아님)
- Orange: 웹에서 제품 문서를 찾지 못했다. 공개 매뉴얼과 사용 경험을 근거로 했다.
- Oracle 공식 Licensing Information User Manual 원문과 SPM 의 라이선스 구분(Enterprise Edition 포함 여부)은 이번 검색에서 확인 못 함.

## 6. 확인 못 함 목록

- Orange 의 Plan/SQL Tuning/Trace 기능은 웹 공식 자료 미확인(로컬 문서 기준).
- SQL Developer 의 Compare Plans, SQL Tuning Advisor UI 상세.
- PL/SQL Developer Explain Plan·Profiler 의 현재 버전 상세와 gather_plan_statistics 지원 여부.
- DataGrip·DBeaver 가 Oracle 에서 plan 을 만드는 방식(PLAN_TABLE 사용 여부 포함).
- DBeaver 그래프 보기가 Oracle 에서 동작하는지.
- SQL Plan Baseline 보기용 딕셔너리 뷰의 팩 구분, SPA 의 라이선스(Real Application Testing 로 알고 있으나 이번 검색에서 확인 못 함).
- 접근 통제 도구가 ALTER SESSION 이나 DBMS_MONITOR 호출을 어떻게 분류하는지.
- Oranda PlanView 에서 PROJECTION 형식 옵션 UI 가 있는지(체크박스 4개만 확인).
