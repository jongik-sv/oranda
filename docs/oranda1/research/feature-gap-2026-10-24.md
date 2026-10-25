# Oranda 기능 빈칸 분석 (주류 Oracle 클라이언트 대비)

- 기준일: 2026-10-24. 기준 커밋: `dev-jji-win144` 의 1ddc07e.
- Oranda 현황은 `docs/PRD.md`, `docs/wbs.md`(Task 125개 중 [xx] 99개), `docs/oranda1/*`, `src/Oranda.App/Features/*`, 명령 ID, `src/Oranda.Oracle/Catalog/**/*.sql` 을 grep 해서 확인했다.
- 경쟁 도구는 공식 문서·릴리스 노트·기능 페이지를 웹에서 찾아 확인했다(4장 출처). 공식 근거를 못 찾은 칸은 「확인 못 함」(표에서는 `?`)으로 두고 추측하지 않았다.

표기:
- ✓ 있음 · △ 일부만 있음 또는 상위 에디션·플러그인이 필요 · ✗ 없음 · `?` 확인 못 함
- Oranda 칸은 「있음/일부/없음」과 근거(파일·명령 ID·PRD ID·Task ID)를 함께 적었다.
- SQL Developer 칸은 데스크톱 26.2, VS Code 확장 26.3, SQLcl 26.3 을 합친 것이다. 데스크톱에만 있는 기능은 「데」로 표시했다.
- Orange 칸은 6.0·7.0 기준이다. (DBA) = DBA Edition 전용.
- Toad 칸의 (DBA모듈)·(Pro)·(Xpert) 는 그 에디션이나 애드온이 필요하다는 뜻이다.

---

## 1. 요약 — 가장 큰 빈칸 10개

1. **PL/SQL 디버거 없음** (PRD D-02 범위 밖). 공식 문서로 확인한 범위에서 Toad·SQL Developer·PL/SQL Developer·DataGrip·Orange 가 모두 갖췄다. Orange 5 사용자 일부가 실제로 썼던 기능이라 대체 저항 요인 1순위다.
2. **스키마 비교·동기화 스크립트, 데이터 비교 없음** (TSK-12-01 백로그 `[ ]`). Toad, SQL Developer(Database Diff·SQLcl DIFF), PL/SQL Developer, DataGrip, DBeaver EE, dbForge Pro 에 있다. 개발→검증→운영 배포 전 확인에 매일 쓴다.
3. **객체 생성·변경 대화상자(테이블·인덱스·시퀀스·권한 마법사) 없음** (SCH-08 「이후」, 코드에 Wizard 없음). 경쟁 도구 전부와 Orange 5 에도 있다. Oranda 에는 컬럼 순서 바꾸기(TSK-13-01)만 있다.
4. **DBA 관리 화면 없음**: 테이블스페이스·공간, 사용자·롤·권한, 스케줄러 잡 관리, alert log, redo/undo. catalog 에 `dba_tablespaces`·`dba_role_privs`·`DBMS_SCHEDULER` 사용이 0건이다. Orange 7 DBA Edition, Toad, SQL Developer 에 있다.
5. **ER 다이어그램 없음** (6.13 후보). Orange ER-Viewer, Toad, SQL Developer Data Modeler, DBeaver, dbForge, PL/SQL Developer(Diagram) 에 있다.
6. **결과 차트·피벗·보고서 없음** (TSK-12-02·12-03 백로그 `[ ]`, 코드에 Chart 0건). PL/SQL Developer Graph, SQL Developer 보고서·Notebook, DataGrip, dbForge 에 있다.
7. **PL/SQL 프로파일러·단위 테스트·커버리지 없음** (PLSQL-07 「이후」, `DBMS_HPROF` 0건). Toad(HPROF·utPLSQL), SQL Developer(HPROF·Unit Test), PL/SQL Developer(Profiler·Test Manager) 에 있다.
8. **실시간 SQL Monitor·ASH·AWR 화면 없음**. `plan.monitor_report` SQL 과 `GetSqlMonitorReportAsync` 는 있지만 이를 부르는 UI 가 없다. 팩 보호장치(SAFE-07)는 이미 있으므로 「팩 보유 선언 접속에서만」 붙이기 쉽다.
9. **테스트 데이터 생성, 문서 생성(HTML) 없음**. 테이블 정의서 xlsx(TableSpec)만 있다. Orange 에 Data Generation 과 Object Report 가 있었으므로 Orange 이전 사용자가 찾을 수 있다.
10. **SSH 터널·Kerberos·TNS 편집기·VCS(Git)·AI 없음**. SSH 는 코드에 0건, Kerberos 는 CONN-10 P2 다. AI 는 확장 지점(APP-09)과 시험 패널만 있다. Toad·SQL Developer·DBeaver·DataGrip 은 SSH 와 Git 을 기본으로 둔다.

**반대로 Oranda 가 앞서는 점**(빈칸 아님, 참고):
- 운영 안전장치: 읽기 전용 프로필, 운영 확인, WHERE 없는 DML 경고, 팩 보호(SAFE-01~08)
- 그리드 편집 행 식별 규칙(6.5.1)
- Thin 드라이버로 Client 설치 0, macOS ARM 지원
- MyBatis 작업 공간(oranda1 MB-01~10)과 MyBatis 로그 재현
- SQL 점검(lint, TSK-13-04)
- 플래시백 조회·복구(TSK-13-05), 변경 전 DDL 보관(ChangeJournal)
- 「내 세션이 남을 막고 있음」 알림(BlockerAlert)
- 계획 비교(PLAN-04)
- Orange `.sqd`·접속 목록 가져오기

---

## 2. 영역별 비교표

열 순서: 기능 | Oranda | Toad | SQL Developer | PL/SQL Developer | DBeaver | DataGrip | Orange

### 2.1 SQL 편집기
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 문맥 자동완성 | 있음 (EDIT-04, Features/Completion) | ✓ | ✓ | ✓ | ? | ✓ | ✓ |
| 입력 중 문법 오류 표시 | 있음 (EDIT-03, ANTLR L2) | ? | ✓(VS Code) | ? | ? | ✓ | ✗(PRD 4.1) |
| 리팩터링(이름 바꾸기·추출) | **없음** (rename/extract 명령 없음) | ✓ | ✓(추출) | ✓ | ? | ✓ | ? |
| 스니펫·템플릿 | 있음 (EDIT-08, Features/Templates, `edit.insertSnippet`) | ✓ | ✓ | ✓ | ? | ✓ | ✓ |
| 포매터·프로필 공유 | 있음 (FMT-01~11, `format.profiles`) | ✓ | ✓ | ✓ | ? | ? | ✓ |
| 코드 접기 | 있음 (`edit.fold*`) | ✓ | ✓(VS Code) | ✓ | ? | ✓ | ✓ |
| 다중 커서 | 있음 (`edit.addCursorAbove/Below`, MultiCaretController). 열 블록 선택은 AvaloniaEdit 기본 기능에 의존하며 코드로 확인 못 함 | ? | ✓(VS Code) | △(열 편집) | ? | ✓ | △ |
| 북마크 | 있음 (`edit.toggleBookmark`) | ? | ✓ | ✓ | ? | ? | ✓ |
| 정의로 이동·DB 객체 찾기 | 있음 (EDIT-05, `nav.*`, Ctrl+P) | ? | ✓ | ? | ? | ✓ | ? |
| 파일·텍스트 비교(diff) | **일부** (PL/SQL 서버 소스 차이 `plsql.showServerDiff`, MyBatis 저장 diff 만 있음. 임의 파일·객체 비교 없음) | ? | ✓(데 Compare and Merge) | ✓ | ? | ✓ | ? |
| 정적 분석(lint) | 있음 (Oranda.Language/Lint, 규칙: CARTESIAN_JOIN·NOT_IN_NULLABLE·IMPLICIT_CONVERSION 등) | △(Pro Code Analysis) | ✓(SQLcl CODESCAN) | ? | ? | ✓(인스펙션) | ? |
| 단축키 프리셋 | 있음 (Orange5·PL/SQL Dev·SQL Dev 프리셋) | ? | ? | ? | ? | ? | — |

### 2.2 실행
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 스크립트·SQL*Plus 호환 | 있음 (SqlPlusCommandProcessor: SET·DEFINE·@·SPOOL·WHENEVER·DESC·EXEC, `exec.runStep`·`runFromCursor`) | ✓ | ✓ | ✓ | ✓ | ? | ✓ |
| 바인드·치환 변수 | 있음 (EXEC-03, Features/Binds) | ? | ✓ | ✓ | ? | ? | ✓ |
| 예상·실제 실행 계획 | 있음 (PLAN-01~05) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 계획 비교 | 있음 (`plan.compare`, PLAN-04) | ? | ? | ? | ? | ? | ? |
| Autotrace·실행 통계 | **일부** (`exec.toggleStatistics`, v$mystat 차이 SessionStatistics.cs. 계획과 통계를 한 화면에 묶은 Autotrace 는 없음) | ? | ✓ | △(Statistics 탭) | ? | ? | △ |
| 실시간 SQL Monitor | **없음** (catalog `plan.monitor_report` 와 백엔드 메서드만 있고 호출하는 UI 가 없음) | ? | ✓ | ? | ? | ? | ✗(Top-N 만) |
| 실행 이력 | 있음 (EXEC-07, HistoryView) | ✓ | ✓ | ✓ | ? | ? | ✓ |
| 이름 붙인 스크립트 폴더 | 있음 (EXEC-09, ScriptsView) | ? | ✓(데) | ? | ? | ? | ✓ |
| 전용 세션 탭 | 있음 (EXEC-13) | ? | ✓ | ? | ? | ? | ? |

### 2.3 결과 그리드
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 인라인 편집 | 있음 (GRID-04·05, PK/ROWID 규칙) | ✓ | ✓ | ✓ | ? | ✓ | ✓ |
| 필터·정렬 | 있음 (GRID-10, `grid.toggleFilterBar`·`toggleQuickFilter`) | ✓ | ✓ | ✓ | ? | ✓ | △ |
| 피벗 | **없음** | ✗(Data Point 에만 있음) | ? | ? | ? | ? | ? |
| 내보내기 형식 | **일부**: CSV·TSV·xlsx·JSON·INSERT (TransferFormat enum). HTML·XML·MERGE·SQL*Loader·Markdown 은 없음 | ✓(HTML·XML·MERGE·SQL*Loader 포함) | ✓(HTML·XML·고정폭·SQL*Loader) | ✓(XML·HTML·Excel) | ✓ | ✓ | ✓(html·xml) |
| 차트 | **없음** (TSK-12-03 `[ ]`) | ? | ✓ | ✓ | △(Lite+) | ✓ | △(DBA) |
| 단일 레코드 보기 | 있음 (GRID-07, `grid.rowView`) | ✓ | ✓ | ✓ | ? | ? | ? |
| LOB 뷰어(텍스트·hex·이미지·JSON·XML) | 있음 (Features/LobRowView, GRID-02) | △ | ✓ | ✓ | ? | ? | ✓ |
| 외래키 따라가기 | 있음 (GRID-11, F4·Shift+F4) | ? | ? | ✓ | ? | ? | ? |
| 결과 두 개 비교 | **없음** (GRID-06 비교는 「이후」) | ? | ? | ? | ? | ✓ | ? |
| 선택 영역 집계·행 수 세기 | 있음 (GRID-09) | ? | ? | ? | ? | ? | ? |

### 2.4 스키마 브라우저·객체 편집
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 객체 트리·상세·데이터 탭 | 있음 (SCH-01·03) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| DDL 보기·일괄 추출 | 있음 (SCH-04·10, DdlBulkExporter) | ✓ | ✓ | ✓ | ? | ? | ✓ |
| 의존 관계 | 있음 (catalog `detail_dependencies`) | ? | ✓ | ? | ✓ | ? | ? |
| 생성·변경 대화상자 | **없음** (SCH-08 「이후」. 컬럼 순서 바꾸기만 있음) | ✓ | ✓ | ✓ | ✓ | ? | ✓ |
| INVALID 일괄 컴파일·휴지통 | 있음 (`schema.compileInvalid`·`recyclebin*`) | ? | ? | ? | ? | ? | ? |
| 소스·열·객체 검색 | 있음 (SCH-09, ObjectSearch) | ? | ? | ? | ? | ? | ✓ |
| 플래시백 조회·복구 | 있음 (Features/Flashback, `grid.flashback.*`) | ? | ? | ? | ? | ? | ? |
| 통계 수집(Analyze) GUI | **없음** (조회만 있음, catalog `detail_stats`) | ? | ? | ? | ? | ? | △(Stats Manager, DBA) |

### 2.5 PL/SQL
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 소스 편집·컴파일·오류 위치 | 있음 (PLSQL-01·02) | ✓ | ✓ | ✓ | ? | ? | ✓ |
| 실행 대화상자(인자 폼) | 있음 (PLSQL-03) | ? | ✓ | ✓(Test Window) | ? | ? | ? |
| 컴파일 경고 | **일부** (`all_errors.attribute` 로 경고를 보이기만 함. `PLSQL_WARNINGS` 를 켜는 설정은 코드에 0건) | ? | ? | ✓ | ? | ? | ✓ |
| 디버거 | **없음** (D-02) | ✓(Base) | ✓ | ✓ | ✗(Oracle) | ✓ | ✓ |
| 프로파일러 | **없음** (PLSQL-07) | ✓(HPROF) | ✓(데 HPROF) | ✓ | ? | ? | ? |
| 단위 테스트 | **없음** | ✓(utPLSQL), △(Code Tester) | ✓(데) | ✓ | ? | ? | ? |
| 코드 커버리지 | **없음** | ? | ? | ? | ? | ? | ? |
| 서버 변경 충돌 확인 | 있음 (PLSQL-04) | ? | ? | ? | ? | ? | △(Check-out) |

### 2.6 비교·이전
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 스키마 비교·동기화 스크립트 | **없음** (TSK-12-01) | ✓ | ✓(Diff·SQLcl DIFF) | ✓ | △(EE) | ✓ | △(ER-Viewer Compare) |
| 데이터 비교 | **없음** | ✓ | ? | ✓ | △(EE) | △ | ? |
| CSV·Excel 가져오기 | 있음 (EXP-02, ImportDialog) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| DB 간 복사·이전 | **없음** | ? | ✓(Database Copy) | ✓(Export/Import Tables) | ✓ | ? | ? |
| Data Pump GUI | 없음 (PRD 비목표) | ✓ | ✓ | ? | ? | ? | ✓ |

### 2.7 모델링·쿼리 빌더
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| ER 다이어그램 | **없음** | ✓ | ✓(Data Modeler) | △ | ✓(편집은 EE) | ? | ✓ |
| 비주얼 쿼리 빌더 | **없음** (6.13 「우선순위 낮음」) | ✓ | ✓ | ✓ | △(Lite+) | ? | ✓ |

### 2.8 성능·DBA
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 세션 목록·Kill·트레이스 | 있음 (SESS-01~05) | ✓ | ? | ✓ | ✓ | ? | ✓ |
| 블로킹 트리·락 | 있음 (SESS-02, `blocking_tree`·`locks`) | ? | ? | △ | ✓ | ? | ✓ |
| Top SQL·바인드 캡처 | 있음 (SQLM-01·02) | △(Shared Pool Browser) | ? | ? | ? | ? | ✓ |
| AWR·ASH·ADDM | **없음** (SQLM-03 「이후」) | △(DBA모듈) | ✓ | ✗ | ? | ? | △(DBA) |
| 인스턴스·대기 이벤트 차트 | **없음** | △(Spotlight) | ✓(Instance Viewer) | ? | ? | ? | △(DBA) |
| 테이블스페이스·공간 | **없음** | ✓ | ✓ | △ | ✓ | ? | △(DBA) |
| 사용자·롤·권한 관리 | **없음** (객체 권한 조회만 있음, `detail_privileges`) | ? | ✓ | △ | ✓(일부 PRO) | ? | △(DBA) |
| 잡·스케줄러 | **일부** (트리에 표시만 함. `DBMS_SCHEDULER` 호출 0건) | ? | ✓ | ✓ | ✓ | ? | ✓ |
| Alert log·트레이스 파일 | **일부** (트레이스 파일 위치만, catalog `trace_file`) | △ | ? | ? | ? | ? | ? |
| Redo·Undo | **없음** (세션 undo 사용량만) | ✓ | ✓ | ? | ? | ? | △(DBA) |
| RMAN | 없음 | ? | ✓(데) | ? | ? | ? | ? |
| SQL 튜닝 어드바이저 | **없음** (Tuning Pack) | ✓ | ✓ | △(AI) | ? | ? | ✓ |
| DB 파라미터·NLS | 있음 (SCH-11, DbInfo) | ? | ✓ | ? | ? | ? | ? |
| 상태 점검 보고서 | **없음** | ? | ? | ? | ? | ? | △(DBA) |

### 2.9 보안·팀·자동화·기타
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| 운영 안전장치(읽기 전용·운영 확인) | 있음 (SAFE-01~08) | ? | ? | ? | ? | ? | △(라벨 색) |
| 감사 정책·민감 컬럼 가림 | **없음** (비밀번호 가림만 있음) | ✓(Unified Audit), △(Sensitive Data, DB Admin) | ✓ | ? | ? | ? | ? |
| VCS(Git) 연동 | **없음** | ✓(Team Coding) | ✓ | ✓ | △(EE) | ✓ | ✗ |
| 공유 스니펫·설정 | **일부** (포매터 프로필·설정 내보내기만) | ✓ | ? | ? | ? | ? | ? |
| 예약 실행·자동화 | **없음** | ✓(Automation Designer) | ✓(SQLcl) | ? | △(EE) | ? | ? |
| CLI | **일부** (`oranda user@alias`·파일 열기만, App.axaml.cs) | ✓ | ✓(SQLcl) | △ | △ | ? | △ |
| 테스트 데이터 생성 | **없음** | △(Pro) | ? | ✓ | △(EE) | ? | ✓ |
| 문서 생성 | **일부** (테이블 정의서 xlsx, TableSpec) | ✓(HTML) | ✓(DB Doc) | △(플러그인) | ? | ? | ✓ |
| AI 보조 | **없음** (APP-09 확장 지점과 ExtensionTest 패널만) | △(AI 구독) | ✓(VS Code·SQLcl MCP) | ✓ | △ | △ | ✗ |
| 한국어 UI | 있음 (Features/Localization, APP-03) | ? | ✓(VS Code) | ? | ✓ | ✓ | ✓ |
| 접근성(낭독기) | **일부** (TSK-10-02 완료, 10-05 남음) | ? | ? | ? | ? | ? | ? |
| MyBatis 매퍼 작업 | 있음 (Features/MyBatisWorkspace) | ✗ | ✗ | ✗ | ? | ? | ✗ |

### 2.10 접속
| 기능 | Oranda | Toad | SQL Dev | PL/SQL Dev | DBeaver | DataGrip | Orange |
|---|---|---|---|---|---|---|---|
| Client 설치 없이 접속 | 있음 (C-02) | △(Direct) | ✓ | ✗ | ✓ | ✓ | ✗ |
| 지갑·ADB | 있음 (CONN-03, WalletImporter) | ✓ | ✓ | △ | △(PRO) | ✓ | ? |
| 프록시 사용자 | 있음 (CONN-02, ProxyUser) | ✓ | ✓ | ✓ | ? | ? | ? |
| tnsnames 읽기 | 있음 (TnsAdmin, TnsNamesImporter) | ✓ | ✓ | ✓ | ? | ✓ | ✓ |
| TNS 편집기 | **없음** | ? | ? | ? | ? | ? | ✓ |
| SSH 터널 | **없음** (코드 0건) | ✓ | ✓(데) | ✗ | ✓ | ? | △ |
| Kerberos·LDAP·토큰 | **없음** (CONN-10 P2) | ✓ | ✓(VS Code) | ? | △(PRO) | ✓ | ? |
| 다른 도구 접속 가져오기 | 있음 (SQL Dev·tnsnames·Orange, CONN-09) | ? | ? | ? | ? | ? | — |

참고 (dbForge Studio for Oracle, 표 밖):
- 전 에디션: 비주얼 쿼리 빌더, 다이어그램, 세션·보안 관리자, AI 도우미
- Standard 이상: 디버거
- Professional 이상: 스키마·데이터 비교와 동기화, VCS
- Enterprise 전용: 데이터 생성기, 문서 생성

---

## 3. 없는 기능과 우선순위 제안

판단 기준:
- 사용자는 Orange 5 를 쓰던 Oracle 개발자다.
- 운영 DB 는 DB 접근 승인 도구가 DML 을 모두 잡아낸다. 그래서 **조회·생성 스크립트 미리보기형 기능은 가치가 높다.** 반대로 **서버에 쓰는 기능(DBA 변경, 동기화 실행)은 「스크립트 생성까지만」이 안전하다.**
- 서버 객체를 설치하지 않는 원칙(C-05)과 라이선스 팩 보호(C-06)를 지켜야 한다.

### P0 (Orange 대체를 막는 빈칸)
| 기능 | 근거 |
|---|---|
| 객체 생성·변경 대화상자 (테이블·인덱스·시퀀스·시노님·권한, DDL 미리보기만 하고 실행은 사용자가 한다) | Orange 5 에도 있던 기능이라 이전 사용자가 바로 찾는다. 「DDL 생성까지만」으로 만들면 승인 도구 흐름과도 맞는다 |
| 스키마 비교 + 동기화 스크립트 생성 (실행 안 함) | 개발→운영 반영 전 차이 확인은 매일 하는 일이다. 대부분의 경쟁 도구에 있다. PRD 도 「1.0 직후 1순위」로 적었다 |
| PL/SQL 디버거 재검토 (D-02) | Orange 5·7, Toad Base, PL/SQL Developer, DataGrip 이 모두 갖췄다. 패키지 위주 개발이면 빠졌을 때 원래 도구로 되돌아갈 가능성이 크다. 사용자에게 사용 빈도를 먼저 조사할 것 |
| `PLSQL_WARNINGS` 켜기 옵션 | PLSQL-02 를 완성하는 작은 작업이다. 지금은 경고를 켤 방법이 없어 목록이 늘 비어 있을 수 있다 |

### P1 (1.0 직후 목표)
| 기능 | 근거 |
|---|---|
| 데이터 비교 (두 테이블·쿼리 결과, 차이 DML 은 생성만) | 기준정보 테이블을 환경별로 비교하는 일이 잦다. 스키마 비교와 엔진을 같이 쓸 수 있다 |
| ER 다이어그램 (역공학·보기 전용) | Orange ER-Viewer 사용자가 이전한다. 읽기 전용이라 안전하다 |
| 테이블스페이스·공간, 사용자·롤·권한, 스케줄러 잡 조회 화면 (변경은 DDL 미리보기) | Orange 7 DBA Edition 대응이다. 조회만으로도 1차 진단 가치가 있다 |
| 실시간 SQL Monitor 보고서 UI (팩 보유 선언 접속에서만) | 백엔드와 catalog 가 이미 있어 비용이 작다 |
| 결과 차트 (막대·선·파이) | TSK-12-03 백로그 항목이다. 생산·품질 집계를 바로 볼 수 있다 |
| 내보내기 형식 추가: HTML·XML·MERGE·Markdown | 보고·위키 붙여넣기용이다. 기존 TransferWriter 구조에 더하기 쉽다 |
| 결과 두 개 비교 (GRID-06) | 쿼리 수정 전후 검증에 쓴다. 고정 탭은 이미 있다 |
| 텍스트·객체 소스 비교(diff) | 서버 diff 코드가 이미 있어 일반화하기 쉽다. PL/SQL Developer·SQL Developer 에 있다 |
| SSH 터널 | 사설망 밖이나 점프 호스트 환경이면 필요하다. 망 구성에 따라 P2 로 내려도 된다(사용자 조사 필요) |
| 테스트 데이터 생성 (개발 DB 전용, INSERT 스크립트 생성) | Orange Data Generation 대응이다. 운영 접속에서는 막아야 한다 |
| 통계 수집(`DBMS_STATS`) GUI, 스크립트 생성형 | Orange Analyze·Stats Manager 대응이며 튜닝 흐름을 마무리한다 |

### P2 (여유 있으면)
| 기능 | 근거 |
|---|---|
| PL/SQL 프로파일러 (`DBMS_HPROF`) | 서버에 결과 테이블을 만들어야 할 수 있다(C-05 충돌을 검토해야 함) |
| utPLSQL 실행·결과 보기 | utPLSQL 이 서버에 설치돼 있어야 한다. 사용 여부를 먼저 확인할 것 |
| 코드 커버리지 (`DBMS_PLSQL_CODE_COVERAGE`) | 경쟁 도구도 대부분 없거나 확인되지 않았다 |
| 비주얼 쿼리 빌더 | PRD 에서도 낮게 두었다. 개발자 페르소나가 잘 쓰지 않는다 |
| 피벗 그리드 | Toad for Oracle 에도 없다 |
| AWR·ASH·ADDM 보고서 (팩 선언 시) | 라이선스 위험이 있다. DBA 페르소나 한정이다 |
| 인스턴스·대기 이벤트 차트, 상태 점검 보고서 | Orange DBA Edition 기능이다. 무료 V$ 뷰로 만들 수 있다 |
| alert log 보기 (`V$DIAG_ALERT_EXT`) | DBA 1차 진단 보조다 |
| HTML 스키마 문서 생성 | 테이블 정의서 xlsx 로 상당 부분을 대체한다 |
| Git 연동 (스크립트 폴더 상태 표시) | 외부 Git 도구로 대체할 수 있다 |
| 예약 실행·자동화 | 운영 DB 에서 무인 실행하면 승인 도구와 부딪힌다. 수요를 확인할 것 |
| AI 보조 (D-04) | 확장 지점(APP-09)이 이미 있다. 운영 접속 기본 차단·전송 미리보기 원칙을 지킬 것 |
| Kerberos·LDAP·토큰 인증 (CONN-10), TNS 편집기 | 조직 인증 방식에 따라 결정한다 |
| 감사 정책 보기·민감 컬럼 가림 | 6.13 후보 |
| 리팩터링 (별칭·식별자 이름 바꾸기) | 문서 안 참조 찾기(EDIT-05)를 확장하면 된다 |
| DB 간 테이블 복사 | 운영 DB 에 쓰는 기능이라 승인 흐름과 충돌한다. 개발 DB 사이에서만 쓰게 할 것 |
| RMAN·redo/undo 관리, Data Pump GUI | PRD 비목표다. 별도 DBA 전용 도구에 맡긴다 |

---

## 4. 출처

### Oranda 저장소
- 문서: `docs/PRD.md`, `docs/wbs.md`, `docs/oranda1/PRD.md`
- 코드: `src/Oranda.App/Features/*`, `src/Oranda.Oracle/Catalog/**`, `src/Oranda.Oracle/Plans/OracleExecutionPlanService.cs`, `src/Oranda.Oracle/SqlPlus/SqlPlusCommandProcessor.cs`, `src/Oranda.Core/Transfer/IDataTransferService.cs`, `src/Oranda.Language/Lint/*`, `src/Oranda.Core/Connection/ConnectionProfile.cs`, `src/Oranda.App/App.axaml.cs`

### Toad for Oracle
- https://support-public.cfm.quest.com/79809_ToadForOracle_2025_R2_ReleaseNotes.pdf
- https://images.g2crowd.com/uploads/attachment/file/62130/expirable-direct-uploads_2Ffa8b0c4a-9210-4c39-9fbb-1d6eb7f729c8_2FHandout-ToadEditions-US-KS-26213.pdf
- https://support.quest.com/kb/4320814/what-are-the-differences-in-each-toad-for-oracle-edition-or-bundle-or-version- (검색 요약으로만 확인)
- https://blog.toadworld.com/wp-content/uploads/2023/07/CustomerEvalGuide-ToadOracleEditions2018R2-Dev.pdf
- https://www.quest.com/documents/toad-for-oracle-trial-evaluation-matrix-developer-datasheet-149194.pdf (검색 요약으로만 확인)
- https://support.quest.com/toad-for-oracle/kb/4269716
- https://forums.toadworld.com/t/generating-a-package/9512
- https://blog.quest.com/product-post/so-you-want-to-export-data-from-oracle-let-s-count-the-ways/
- https://forums.toadworld.com/t/crosstab-query/6025
- https://www.quest.com/community/b/en/posts/why-profile-my-code
- https://support.quest.com/kb/4230761
- https://www.quest.com/documents/toad-for-oracle-db-admin-module-datasheet-146117.pdf (검색 요약으로만 확인)
- https://blog.quest.com/product-post/how-to-monitor-database-activity-with-the-toad-database-browser-utility/
- https://www.databasejournal.com/oracle/increasing-productivity-with-toad-for-oracle/
- https://forums.toadworld.com/t/toad-modeler-for-schema-documentation/53886
- https://support.quest.com/kb/4332451
- https://support.quest.com/toad-for-oracle/kb/204845
- https://blog.quest.com/product-post/connecting-to-databases-with-mfa-in-toad-kerberos-ssh-wallet-and-more/

### Oracle SQL Developer, VS Code 확장, SQLcl, Data Modeler
- https://docs.oracle.com/en/database/oracle/sql-developer/26.2/rptug/toc.htm
- https://www.oracle.com/database/technologies/appdev/sqldev/history.html
- https://www.oracle.com/tools/sqldeveloper/support.html
- https://www.dbvis.com/?p=27641 (2차 출처: 데스크톱 26.2 가 마지막 릴리스라는 내용)
- https://docs.oracle.com/en/database/oracle/sql-developer-vscode/26.3/sqdnx/toc.htm
- https://www.oracle.com/database/sqldeveloper/vscode/features/
- https://marketplace.visualstudio.com/items?itemName=Oracle.sql-developer
- https://docs.oracle.com/en/database/oracle/sql-developer-command-line/26.3/sqcug/toc.htm
- https://www.oracle.com/tools/downloads/sql-data-modeler-downloads.html

### PL/SQL Developer
- https://www.allroundautomations.com/products/pl-sql-developer/
- https://www.allroundautomations.com/?p=1650
- https://www.allroundautomations.com/?p=5262 (16.0)
- https://www.allroundautomations.com/?p=5718 (17.0)
- https://www.allroundautomations.com/products/pl-sql-developer/features/sql-window/
- https://www.allroundautomations.com/products/pl-sql-developer/features/pl-sql-editor/
- https://www.allroundautomations.com/products/pl-sql-developer/features/command-window/
- https://forums.allroundautomations.com/threads/using-oracle-proxy-users.13795 (검색 요약으로만 확인)

### Orange for ORACLE
- https://www.warevalley.com/ko/product/orange
- https://www.warevalley.com/en/product/orange
- https://www.warevalley.com/onlineupdate/manual/ko/6/OrangeforOracle_UserGuide_kr.pdf
- https://www.tangunsoft.com/marketplace/detail?id=1609&tab=W
- http://www.press9.kr/news/articleView.html?idxno=36681
- https://sites.google.com/warevalley.com/orangefaq/home/기능문의

### DBeaver
- https://dbeaver.com/edition/
- https://dbeaver.com/docs/dbeaver/Oracle/
- https://dbeaver.io/about/
- https://dbeaver.com/docs/dbeaver/Kerberos-Authentication/
- https://dbeaver.com/docs/dbeaver/AI-Smart-Assistance/
- https://dbeaver.com/docs/dbeaver/Task-Scheduler/
- https://dbeaver.com/docs/dbeaver/Command-Line/
- https://dbeaver.com/docs/dbeaver/UI-Language/
- https://dbeaver.com/docs/dbeaver/PGDebugger/

### DataGrip
- https://lp.jetbrains.com/features-overview/
- https://www.jetbrains.com/help/datagrip/oracle.html
- https://www.jetbrains.com/help/datagrip/debugging-code.html
- https://www.jetbrains.com/help/datagrip/connect-to-oracle-cloud-by-using-wallets.html
- https://www.jetbrains.com/help/datagrip/query-execution-plan.html
- https://www.jetbrains.com/help/datagrip/schema-comparison-and-migration.html
- https://www.jetbrains.com/help/datagrip/compare-data.html
- https://www.jetbrains.com/help/datagrip/multicursor.html
- https://www.jetbrains.com/help/datagrip/refactoring-source-code.html
- https://www.jetbrains.com/help/datagrip/language-and-region.html

### dbForge Studio for Oracle, Navicat
- https://www.devart.com/dbforge/oracle/studio/editions.html
- https://www.devart.com/dbforge/oracle/studio/features.html
- https://docs.devart.com/studio-for-oracle/getting-started/connecting-to-db.html
- https://www.navicat.com/en/products/navicat-for-oracle

### 한계
- Quest 문서는 대부분 JS 로 렌더링돼 직접 읽지 못한 것이 많다. 그래서 Toad 에디션 판정은 2025 R2 릴리스 노트와 2017 handout 에 의존한다.
- DBeaver Community 열은 공식 비교표에 없어서 Oracle 문서와 Lite 열로 추정했다.
- Orange 5.0 원본 자료와 Orange 8 기능 자료는 웹에서 찾지 못했다. Orange 5.0 은 공개 매뉴얼과 사용 경험을 기준으로 삼았다.
- 웹에서 확인한 범위 밖의 「통념」은 표에 넣지 않고 `?` 로 두었다.
