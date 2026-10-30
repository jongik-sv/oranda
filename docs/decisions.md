# Decisions Log — project

> Append-only audit trail of autonomous decisions made during DDTR/feat/wbs cycles.
> Edit prior entries forbidden — record reversals as new entries instead.

## D-001 (2026-10-19T05:26:38Z)
- **Phase**: prd-resolve
- **Decision needed**: TRD.md prd-validate vague_metric 10건 처리
- **Decision made**: 오탐으로 판정, 메서드 이름 유지, TRD에 Assumptions 절 추가
- **Rationale**: 모든 지적이 C# …Async 메서드 이름의 'eAsy' 부분 문자열 일치였고, 정량 표현 누락이 아님
- **Reversible**: yes
- **Source**: docs/TRD.md 6장

## D-002 (2026-10-19T05:31:55Z)
- **Phase**: wbs-resolve
- **Decision needed**: wbs-validate vague_action 13건, 셸 fan-in 10
- **Decision made**: 모호 동사를 구체 동사로 교체, 도구 창 9개 의존을 셸 구현(TSK-03-01)에서 셸 계약(TSK-02-05)으로 이동, 공정 앞쪽 체인 깊이 5는 예외로 기록
- **Rationale**: 도구 창은 자동 등록 계약만으로 개발 가능, 기술 검증 결과가 계약에 들어가야 함
- **Reversible**: yes
- **Source**: docs/wbs.md 의존 그래프

## D-003 (2026-10-19T05:53:16Z)
- **Phase**: design
- **Decision needed**: T-D01 런타임: .NET 8 지원 종료(2026-11-10) 대응
- **Decision made**: .NET 10 + Avalonia 12.1.4로 전환, .NET 8 SDK 삭제, System.Security.Cryptography.Pkcs 10.0.13 직접 참조
- **Rationale**: 사용자 지시(2026-10-19). 빈 창 시작 2.3 s→1.8~1.9 s, 빌드 경고 0, 테스트 10개 통과
- **Reversible**: yes
- **Source**: docs/TRD.md 2.1

## D-004 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-02 솔루션 재구성
- **Decision needed**: ODP.NET 의존 코드와 Core 경계, Oracle 단위 시험 위치, 아키텍처 검사 방식
- **Decision made**: Core 에는 순수 모델만 둔다(`ConnectionProfile` record). 연결 문자열 조립·프로세스 설정은 `Oranda.Oracle` 의 확장 메서드로 옮긴다. DB 없이 도는 Oracle 시험은 `Oranda.Oracle.IntegrationTests` 에 Category 없이 두고, 실제 DB 시험만 `[Trait("Category","Integration")]` 를 붙인다. 아키텍처 검사는 외부 패키지 없이 어셈블리 참조와 csproj 참조를 함께 본다
- **Rationale**: 컴파일러가 쓰지 않는 참조를 어셈블리에서 지우므로 어셈블리 검사만으로는 위반을 놓친다. 빌드 경고 0, 시험 17개 통과
- **Reversible**: yes
- **Source**: 커밋 2eaf738

## D-005 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-03 실제 DB 접속 시험
- **Decision needed**: 컨테이너 시험 실행 방식, ServerProbe 권한 판정
- **Decision made**: Integration 시험은 test csproj 기본 필터(`VSTestTestCaseFilter=Category!=Integration`)로 빼고, 명령줄 `--filter Category=Integration` 으로만 돌린다. podman 에서는 Testcontainers 로그 대기 대신 접속 재시도로 준비 여부를 판정한다(`DOCKER_HOST=npipe://./pipe/docker_engine`, `TESTCONTAINERS_RYUK_DISABLED=true`). 19c·ADB 시험은 `ORANDA_IT_19C_CONNSTR`·`ORANDA_IT_ADB_CONNSTR`·`ORANDA_IT_ADB_WALLET` 이 없으면 아무것도 하지 않는다. `CanTraceSessions` 는 DBMS_MONITOR EXECUTE 행 존재 또는 EXECUTE ANY PROCEDURE 로 판정한다
- **Rationale**: 26ai(gvenzl/oracle-free:23-slim)에서 버전 23·FREEPDB1·권한 점검이 실제 값과 일치. 기존 `CanRunAsync` 판정은 권한 없는 계정도 true 로 냈다
- **Reversible**: yes
- **Source**: 커밋 a727e94, e736a79, spikes/TSK-01-03/RESULT.md. 보류: 별도 19c, ADB, macOS ARM

## D-006 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-04 시작 시간 측정 (T-D07)
- **Decision needed**: 게시 방식과 1.5 s 목표
- **Decision made**: 게시본은 ReadyToRun composite(self-contained). 트리밍·Inter 글꼴 제거·TieredPGO 끄기는 쓰지 않는다. ODP.NET 은 시작 경로 밖에서 지연 로딩한다. 1.5 s 목표는 「기준 PC 전체 1.5 s + 프로세스 내부(main → input-ready) 1.3 s」로 나누고, 판정 지표는 실제 첫 프레임 또는 input-ready 로 한다. T-D07: 1.0 은 R2R composite 유지, NativeAOT 는 ODP.NET 경고 53건이 해소될 때까지 보류
- **Rationale**: 이 PC 의 input-ready 중앙값 R2R 2276 ms → composite 1670 ms, 내부 구간 1.17~1.32 s. Main 전 약 430 ms 는 이 PC 의 첫 창 비용(백신·IME 추정). 트리밍은 시간 효과 없음
- **Reversible**: yes
- **Source**: 커밋 fd95ade, spikes/TSK-01-04/RESULT.md. 보류: macOS, 보안 SW 없는 PC 재측정, NativeAOT 실행 측정(MSVC 필요)

## D-007 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-05 에디터 성능 시험 (D-05, T-D03)
- **Decision needed**: 에디터 컴포넌트, 다중 커서 방식, 키 입력 수락 기준
- **Decision made**: D-05 = Avalonia.AvaloniaEdit 12.0.0 + 자체 L1 증분 렉서(11.4.1 은 Avalonia 12 에서 TypeLoadException 으로 실행 불가). T-D03 = 다중 커서는 포크 없이 확장(앵커 + 배경 렌더러 + 터널 입력)으로 만들고, 1.0 범위는 입력·삭제·Enter·Alt+클릭·Ctrl+Alt+↑/↓·열 선택. 「키 입력 반영 16 ms(p95)」는 렌더 틱 대기를 뺀 처리 시간으로 정의한다. L2 ANTLR 은 앱 시작 때 백그라운드에서 데운다
- **Rationale**: 1만 줄 패키지 처리 p95 4.6~6.2 ms(커서 50개 14.2 ms), 60 Hz 틱 대기 포함 21~23 ms. L1 증분 렉싱 0.0003~1.04 ms. ANTLR 콜드 2.1~2.5 s, 데운 뒤 문장 1~2 ms
- **Reversible**: yes
- **Source**: 커밋 1f90b3e, spikes/TSK-01-05/RESULT.md. 재확인: 400~540 ms 튀는 렌더 표본(조용한 PC)

## D-008 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-06 가상 그리드 시험 (T-D02)
- **Decision needed**: 결과 그리드 방식, 셀 텍스트 캐시
- **Decision made**: 직접 그리기 가상 그리드로 확정. TreeDataGrid.Avalonia 포크는 대안에서 뺀다. 셀 텍스트 캐시는 FormattedText 가 아니라 GlyphRun(ASCII) + TextLayout(그 밖)으로 한다. 페이지 캐시 기준은 행당 약 1 KB(50열), 200 MB 상한이면 약 20만 행
- **Rationale**: Render() UI 스레드 평균 0.9~6.1 ms. 같은 시나리오에서 TreeDataGrid 포크 3.9~22.4 fps. Avalonia 12 FormattedText 는 그릴 때마다 다시 포맷해 캐시 효과가 없음(셀당 215 µs 대 GlyphRun 3 µs)
- **Reversible**: yes
- **Source**: 커밋 34866eb, spikes/TSK-01-06/RESULT.md. 미확인: 60 fps(측정 PC 가 RDP 32 Hz). 로컬 콘솔에서 `spikes/TSK-01-06/run-measure.ps1` 재실행 필요

## D-009 (2026-10-19T08:00:00Z)
- **Phase**: TSK-01-07 CI·서명·성능 측정 도구
- **Decision needed**: CI 구성, 서명 조건, 시작 측정 훅
- **Decision made**: CI 잡 3개(windows-latest 빌드·시험·Perf, macos-14 빌드·패키징·서명, ubuntu Integration). 서명·공증은 `APPLE_CERTIFICATE_P12_BASE64` 가 있을 때만 돌고 없으면 무서명 패키징. 시작 측정 표식은 `ORANDA_PERF=1`(stdout)·`ORANDA_PERF_FILE`(파일) 두 가지, CI 는 파일 방식. 성능 예산 강제(`--enforce`)는 예산 확정 전까지 끈다
- **Rationale**: WinExe 는 stdout 이 막힐 수 있다. 비밀값 없이도 CI 가 통과해야 한다
- **Reversible**: yes
- **Source**: 커밋 71ce5ee, spikes/TSK-01-07/RESULT.md. 보류: Apple 비밀값 8개 등록, Windows Authenticode 인증서, macOS 주 1회 측정 스케줄

## D-010 (2026-10-19T12:00:00Z)
- **Phase**: TSK-02-01 로컬 데이터 모델
- **Decision needed**: 라이선스 팩 표현, 포매터·레이아웃 저장 형식, 이력 검색·비밀 처리
- **Decision made**: 접속의 라이선스는 열거형 `LicensePack { None, Diagnostics, DiagnosticsAndTuning }`. 포매터 프로필은 `id`·`name` + `options`(= FormatterOptions). 레이아웃은 Dock 형식이 아닌 App DTO `LayoutDocument`. 이력 검색은 FTS5 trigram. 이력 SQL 은 `ISecretRedactor` 로 비밀을 가린 뒤 저장하고 바인드 값은 기본 저장 안 함. 비밀은 `credentialRef = oranda:conn:{id}` 키로 DPAPI/키체인에만. `settings.json` 추가, 마이그레이션 전 `backup/`
- **Rationale**: Tuning 은 Diagnostics 를 전제. 비밀번호가 어떤 파일에도 남지 않음을 자동 검사로 확인 가능
- **Reversible**: yes
- **Source**: 커밋 733bd02, docs/design/data-model.md

## D-011 (2026-10-19T12:00:00Z)
- **Phase**: TSK-02-02 화면 구조·명령 체계
- **Decision needed**: 도킹 라이브러리, 결과 그리드 위치, 키맵 형식, 환경 라벨
- **Decision made**: Dock.Avalonia 12.1.x(Avalonia 12.1.4 지원), `Dock.*` 형식은 `Shell/Docking` 안에서만. 결과 그리드는 도구 창이 아닌 SQL 편집기 문서의 아래쪽 분할. 명령 ID `oranda.<영역>.<동작>`, 기본 단축키는 키맵 파일에만. 키맵 프리셋 4종(default·orange5·plsqldev·sqldev), `Primary` = Win Ctrl / mac Cmd, `mac` 별도 목록. 운영 라벨은 빨강 고정(사용자 색 무시), 확인 대화상자 여부는 색이 아닌 환경 값으로 결정
- **Rationale**: Dock.Avalonia 12.1.0.12 가 Avalonia 12.1.4 에 의존(nuget 확인). 키맵 4종 Windows·macOS 충돌 0
- **Reversible**: yes
- **Source**: 커밋 21bece1, docs/design/ui-shell.md·commands.md·keymaps/. 보류: PL/SQL Developer·SQL Developer 키 실물 대조(TSK-04-07)

## D-012 (2026-10-19T12:00:00Z)
- **Phase**: TSK-02-03 Core 계약
- **Decision needed**: 서비스 등록 방식, TRD 6장 시그니처 변경, 여러 Task 가 한 인터페이스를 구현할 때
- **Decision made**: 서비스 등록은 `[OrandaService]` 특성 + `[assembly: OrandaServiceAssembly]` 스캔(Core 자체 구현 EventBus·ExecutionPolicy 는 앱 조립 코드가 직접 등록). `ExecuteAsync`·`ExecuteScriptAsync` 에 `ExecutionOptions?` 추가, `ISession` 에 Purpose·DbKey 추가, IMetadataProvider·IMonitorService 메서드 추가(TRD 6장 반영). 한 인터페이스를 여러 Task 가 구현하면 partial 파일로 나눈다. 공용 가짜 객체는 `tests/Oranda.Testing`
- **Rationale**: 기능 Task 가 공유 진입점에 줄을 더하지 않고, 두 Task 가 한 파일을 나눠 갖지 않게
- **Reversible**: yes
- **Source**: 커밋 d988875, docs/design/design.md

## D-013 (2026-10-19T12:00:00Z)
- **Phase**: TSK-02-04 SQL 카탈로그
- **Decision needed**: 머리 주석 형식, 선택 규칙, 시험 위치
- **Decision made**: 필수 `id`·`min-version`·`license`, 선택 `requires`·`rac`. 같은 id 중 서버 주 버전 이하 최대 min-version 선택(하한 19). `license` 는 카탈로그 전용 `CatalogLicense { None, Diagnostics, Tuning }`(필요한 팩), 접속 `LicensePack`(보유 조합)과 대조는 정책 관문. 파서·검사는 Core, 임베디드 수집은 Oracle. 시험은 새 `tests/Oranda.Oracle.Tests`(Category=Catalog, 기본 실행 포함)
- **Rationale**: Core.Tests 가 Oracle 을 참조하면 드라이버를 끌고 옴. 기능 Task 는 .sql 파일만 더하면 됨
- **Reversible**: yes
- **Source**: 커밋 7bd662e, docs/design/catalog.md

## D-014 (2026-10-19T12:00:00Z)
- **Phase**: TSK-02-05 UI 셸 계약
- **Decision needed**: 셸 기여 등록, 키맵 프리셋 정본 위치, Dock 패키지 도입 시점, 환경 라벨 값
- **Decision made**: `[OrandaCommand]`·`[OrandaToolPanel]`·`[OrandaStatusBarItem]` + App 스캔. 기능 Task 는 `src/Oranda.App/Features/<폴더>/` 하나를 통째로 소유. 키맵 프리셋 정본은 `docs/design/keymaps/*.json` 하나, App 은 링크해 임베디드 리소스로. Dock.Avalonia 패키지·레지스트리 배선은 TSK-03-01 에서 시작 시간 측정과 함께. 환경 라벨은 Development·Test·Production 만, 모르는 값은 Production 으로 취급
- **Rationale**: 셸 파일 수정 없이 도구 창이 잡힘. 시작 경로(D-006)를 이번 Task 에서 건드리지 않음. 모르는 값은 안전 쪽으로
- **Reversible**: yes
- **Source**: 커밋 86f6f72, docs/design/ui-contract.md

## D-015 (2026-10-19T12:00:00Z)
- **Phase**: WP-02 마감(조정자)
- **Decision needed**: AuthMode 에 더한 SysBackup·SysDg·SysKm 이 연결 문자열에서 SYSOPER 로 바뀌던 문제
- **Decision made**: `BuildConnectionString` 이 모드마다 DBA Privilege 를 그대로 넣게 고침(SYSBACKUP·SYSDG·SYSKM). ODP.NET Managed 23.26 연결 문자열 빌더가 세 값을 받는 것을 시험으로 확인. 실제 서버 접속 확인은 TSK-03-03
- **Rationale**: 잘못된 권한으로 조용히 접속하는 것을 막음
- **Reversible**: yes
- **Source**: WP-02 마감 커밋

## D-016 (2026-10-20T09:00:00Z)
- **Phase**: 편집기 탭 닫기
- **Decision needed**: 고친 SQL 탭을 닫을 때 동작
- **Decision made**: 고친 SQL 탭 닫기는 저장 / 저장 안 함 / 취소를 묻는다. 이름 없는 탭은 「저장」이 다른 이름으로 저장이다. 작업 공간 닫기와 앱 종료는 묻지 않는다. 대상은 SQL 탭만이다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: SQL Developer·DBeaver 와 같은 기대를 맞추되 종료 흐름은 막지 않음
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-017 (2026-10-20T09:00:00Z)
- **Phase**: 편집기 세션
- **Decision needed**: 전용 세션 SQL 탭(SQL Developer 의 unshared worksheet) 도입 방식
- **Decision made**: 탭마다 자기 세션을 가진다. 닫을 때 미커밋 변경이 있으면 커밋 / 롤백 / 취소를 묻는다. 상태 표시줄에 전용 세션임을 표시한다. 시작 복원 때는 공유 세션 탭으로 되살린다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 세션 상태가 탭에 묶이므로 닫기 확인이 필요하고, 복원 때 연결을 몰래 여러 개 열지 않음
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-018 (2026-10-20T09:00:00Z)
- **Phase**: 범위
- **Decision needed**: 스키마 비교·보고서·차트
- **Decision made**: 이번 회차에 구현하지 않고 wbs 백로그 Task 로만 등록한다(TSK-12-01~03). 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 일정 안에 들어가지 않으며 우선순위가 낮음
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-019 (2026-10-20T09:00:00Z)
- **Phase**: 시작 복원
- **Decision needed**: 편집기를 되살릴 때 복원 범위
- **Decision made**: 선택 영역은 버리고 커서 위치만 복원한다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 선택 복원은 이득이 작고 어긋난 위치에서 오히려 혼란을 줌
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-020 (2026-10-20T09:00:00Z)
- **Phase**: TSK-04-09
- **Decision needed**: PRD 7.2 「입력 멈춘 뒤 300 ms」 해석과 디바운스
- **Decision made**: 「입력 멈춘 뒤」 는 마지막 키를 기준으로 읽는다. 따라서 EditorDiagnostics.Debounce 를 300 ms 에서 100 ms 로 줄인다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 디바운스만 300 ms 면 분석 시간을 더하기 전에 한도를 다 쓰게 됨
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-021 (2026-10-20T09:00:00Z)
- **Phase**: TSK-06-11
- **Decision needed**: PRD 7.2 취소 반응 측정 조건(선택지 나)
- **Decision made**: 취소 측정 조건은 OOB 가 통하는 실제 서버 경로로 한다. gvenzl 이미지 기본 sqlnet.ora 의 DISABLE_OOB 경로는 측정 대상이 아니다. CI 의 ORANDA_IT_CANCEL_STRICT 는 끈 채로 둔다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 앱은 취소 즉시 Break 를 보내며(단위 시험 확인) 지연은 시험 컨테이너 설정 탓임
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-022 (2026-10-20T09:00:00Z)
- **Phase**: TSK-10-06
- **Decision needed**: composite R2R 과 설치 파일 80 MB
- **Decision made**: macOS 에서 composite 와 어셈블리별 R2R 의 시작 시간 차가 0.3 s 이하이면 composite 를 끈다. 측정 결과로 확정한다(P4 에서 기록). 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: composite 는 시작 −0.5~0.6 s 의 대가로 설치 파일이 약 29 MB 커짐
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-023 (2026-10-20T09:00:00Z)
- **Phase**: TSK-06-12
- **Decision needed**: PRD 7.2 「추가 메모리 행당 200 bytes」 정의
- **Decision made**: 데이터를 포함한 값이며, 기준 데이터셋은 PRD 의 6열(NUMBER·VARCHAR2·NUMBER·DATE·NUMBER·VARCHAR2)이다. 가져온 행 기준이다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 데이터를 빼면 측정 기준이 열 수에 따라 흔들림
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-024 (2026-10-20T09:00:00Z)
- **Phase**: 기타 확정
- **Decision needed**: GRID-12/13 우선순위, 새 행만 있을 때 정렬, 템플릿 줄임말 규칙, SCH-01 위치
- **Decision made**: GRID-12/13 은 PRD 205·206행 그대로 P1/M2 로 두고 wbs Task 를 더하지 않는다. 새 행(삽입)만 있어도 정렬을 막는다(GridEditSession.IsDirty 가 _inserts 를 포함, GridEditTests.Sort.cs 로 확인). 템플릿 줄임말 규칙은 지금 구현(TemplatesViewModel.cs 291~307행: 영문·숫자·밑줄, 대소문자 무시 중복 금지, 커밋 c58c6d6)을 따른다. SCH-01 위치도 지금 구현을 따른다. 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 이미 구현·검증된 동작을 정본으로 삼아 문서와 맞춤
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시)

## D-025 (2026-10-20T10:00:00Z)
- **Phase**: TSK-10-06 composite 판정(D-022 적용 결과)
- **Decision needed**: D-022 규칙(macOS 에서 composite 와 어셈블리별 R2R 의 시작 시간 차가 0.3 s 이하이면 composite 끔)을 어떤 값으로 적용할지
- **Decision made**: macOS 는 측정하지 않는다. composite 는 win-* RID 전용이라 macOS 값이 Windows 설치 파일 결정을 대변하지 못하고, 실제 창이 포커스를 가져가 사용자 입력을 방해할 위험이 있다. 기존 Windows 실측(spikes/TSK-01-04/RESULT.md, input-ready 1670 ms 대 2276 ms, 차 −0.5~0.6 s)에 D-022 규칙을 적용하면 차가 기준 0.3 s 보다 크므로 composite 를 유지한다. 80 MB 목표는 composite 를 끄는 대신 다른 수단으로 줄인다(후보는 wbs TSK-10-06 note). 사용자가 결정을 조정자에게 위임(2026-10-20), 뒤집을 수 있음
- **Rationale**: 시작 시간 −0.5~0.6 s 는 PRD 시작 목표(1.5 s)와 직결되고, 크기는 다른 수단이 남아 있음
- **Reversible**: yes
- **Source**: 조정 세션 확정(perf-decisions 레인 지시 6), D-022 의 적용 결과 기록(D-022 본문은 고치지 않음)

## D-026 (2026-10-20T14:00:00Z)
- **Phase**: 지원 서버 범위 변경(D-06 일부 변경)
- **Decision needed**: D-06 에서 「나중 검토」 로 둔 Oracle 11.2 접속을 1.0 에 넣을지
- **Decision made**: 넣는다. 11.2 만 허용하고 12c·18c·21c·11.1 이하는 계속 막는다. 드라이버는 ODP.NET Core 23.26.301 하나만 쓰고, 11g 차이는 SQL 카탈로그 min-version: 11 변형과 기능 끄기로 흡수한다(TSK-03-07)
- **Rationale**: 시험 DB(11.2.0.3)에서 ODP.NET 23 접속·조회가 확인됐고(구버전 드라이버 불필요), 카탈로그 57개 중 깨지는 것은 FETCH FIRST·12c+ 열을 쓰는 15개뿐이라 PRD 11.3 의 「적은 비용」 경우에 해당한다. 현장에 11.2 DB 가 남아 있음
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-20, 「11g 만들고 진행하자」)

## D-027 (2026-10-20T09:30:00Z)
- **Phase**: 작업 배정(PC 간 분담)
- **Decision needed**: idea 추가 기능 8건(WP-13)을 어느 PC 가 할지
- **Decision made**: mac(맥북 조정 세션)이 한다. wbs 에 제목 [mac]·tags mac·note 「담당 PC: mac」 으로 표시하고, win144 세션은 이 Task 들을 하지 않는다(반대로 [win144] 표시 Task 는 mac 이 하지 않는다)
- **Rationale**: 두 PC 가 같은 wbs 를 함께 고치므로 담당 표시로 겹침을 막음
- **Reversible**: yes
- **Source**: 사용자 요청(2026-10-20, 「wbs에 등록하고 진행하자. 새로 등록한 것은 mac 용이라고 표시를 해야 된다」)

## D-028 (2026-10-20T16:00:00Z)
- **Phase**: 작업 공간 파일 이름 규칙(EDIT-09)
- **Decision needed**: 작업 공간을 `이름.md` 로 저장하면 일반 Markdown 과 섞인다. 이름만 보고 작업 공간이면서 Markdown 인 것을 알 수 있는 규칙
- **Decision made**: 새로 저장하는 작업 공간 파일은 `이름.workspace.md`. 앱 안의 이름은 `.workspace` 를 뗀다. 예전 `이름.md` 작업 공간은 그대로 열고 그 경로에 다시 쓴다(다른 이름으로 저장하면 `.workspace.md` 를 제안). 파일 열기 필터 첫 항목은 「Oranda 작업 공간 (*.workspace.md)」. `.sqd`·MyBatis 에서 저장하면 `같은 이름.workspace.md` 를 제안한다
- **Rationale**: 끝 확장자가 `.md` 라 어느 편집기·GitHub 에서도 Markdown 으로 보이고, 이름에 workspace 가 보인다. 전용 확장자(.osw 등)는 Markdown 인지 알 수 없다. 대가: OS 파일 연결은 마지막 확장자로만 되므로 더블 클릭으로 Oranda 가 뜨지는 않는다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-20, 「작업공간 이름 규칙을 추천 대로 진행해」), 검토 문서 https://claude.ai/code/artifact/b267e982-d892-46b2-9203-4b7236f80d02

## D-029 (2026-10-20T18:00:00Z)
- **Phase**: TSK-03-07 Oracle 11.2 지원 구현 중 정한 것
- **Decision needed**: 버전에 따른 기능 켜짐을 어디에 둘지, 카탈로그 min-version 을 어떻게 적을지, 11.2 에 없는 기능 처리
- **Decision made**: (1) 서버 버전으로 정해지는 기능 켜짐은 권한용 `ServerCapabilities` 와 나눠 `ServerInfo.Features`(ServerFeatures)에 둔다. (2) 카탈로그 min-version 은 그 문장이 실제로 도는 가장 낮은 버전으로 적는다: 11g 에서 도는 파일은 11, 12c 이후 문법·열을 쓰면 19 와 `.11.sql` 변형을 함께 둔다. 빌드 검사 규칙 `pre12c-feature` 가 11 변형의 FETCH FIRST·OFFSET·12c+ 열을 막는다. (3) `session.my_blocking` 은 19 이상 전용으로 두고 11.2 에서는 기능을 숨긴다
- **Rationale**: 권한과 버전은 바뀌는 이유가 달라 섞으면 끄기 조건이 엉킨다. min-version 을 실제 하한으로 적어야 새 파일이 11g 를 자동으로 덮는지 빌드 검사가 알 수 있다
- **Reversible**: yes
- **Source**: TSK-03-07 레인 결정 후보, 조정 세션 확정

## D-030 (2026-10-20T19:00:00Z)
- **Phase**: 지원 서버 범위 변경(D-06·D-026 일부 변경)
- **Decision needed**: 시험하지 않은 12c·18c·21c 를 접속 관문에서 막을지
- **Decision made**: 막지 않는다. 버전 관문은 11.2 미만(11.1 이하)만 막는다. 시험한 버전(11.2·19c·23ai/26ai·ADB)은 PRD 에 따로 적는다. 카탈로그는 버전 이하 중 가장 높은 min-version 을 고르므로 12c·18c 는 11 변형, 21c 는 19 변형을 쓴다
- **Rationale**: 시험을 못 했을 뿐 일부러 막을 이유가 없다. 쓰는 사람이 기존 도구 대신 Oranda 를 쓸 수 있고, 문제가 나면 결함으로 받는다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-20, 「12c, 18c, 21c를 일부러 막을 필요는 없잖아. 테스트를 못할 뿐이지. 그냥 놔둬」)

## D-031 (2026-10-20T20:00:00Z)
- **Phase**: TSK-03-07 후속, 11g 실행 중 취소(OOB)
- **Decision needed**: 개발 PC 의 11g 컨테이너에서 실행 중 취소가 끝나지 않아 `OracleConfiguration.DisableOOB` 를 앱 기본으로 켤지
- **Decision made**: 켜지 않는다(드라이버 기본값 유지). `DisableOOB` 는 컨테이너 통합 시험(`ORANDA_IT_11G_DISABLE_OOB=1`)에서만 쓴다
- **Rationale**: 시험 DB(11.2.0.3)에 실제 망으로 붙어 60 초 넘게 도는 SELECT 를 3 초 뒤 취소하니 OOB 켜짐·꺼짐 모두 ORA-01013 이 5~22 ms 안에 오고 세션도 다시 쓸 수 있었다. 끝나지 않던 것은 podman 포트 중계가 긴급(OOB) 데이터를 버리는 개발 PC 환경 문제다. PL/SQL 대기(DBMS_LOCK)는 그 계정에 권한이 없어 확인하지 못했다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-20, 「OOB도 추천대로 할께」 = 실망에서 먼저 확인), 조정 세션 측정

## D-032 (2026-10-20T23:00:00Z)
- **Phase**: TSK-03-08 동시 접속 여러 개(CONN-06) 구현 중 정한 것
- **Decision needed**: 새 접속·탭·콤보·끊김·종료 때 어느 접속을 쓰고 무엇을 물을지
- **Decision made**: (1) 새 접속 = 지금 접속이 되고 활성 SQL 탭이 따라간다(콤보로 고른 것과 같음). 다른 탭은 그대로. 그 접속을 쓰는 탭이 이 탭뿐이고 실행 중이거나 미커밋이면 옮기지 않는다. (2) 프로필당 공유 작업 세션 1개: 이미 열린 프로필에 다시 접속하면 지금 접속만 바꾼다. 끊긴 것은 같은 번호 자리에 다시 연다. (3) 콤보 번호 = 연 순서(1부터), 닫으면 뒤 번호가 당겨진다. 위치 = 툴바 맨 왼쪽, 상한 10개. (4) 접속 안 됨 탭에서 실행하면 「다시 접속 / 지금 접속으로 바꾸기 / 취소」 를 묻는다. (5) 접속 프로필이 없는 탭 = 지금 접속을 따른다(접속 하나일 때 예전 동작). (6) 끊긴(broken) 접속의 미커밋은 묻지 않고 서버가 되돌렸다고 알린다. (7) 창 제목 [운영] = 운영 접속이 하나라도 열려 있으면 붙인다
- **Rationale**: Orange 의 툴바 접속 콤보 동작을 따르되, 실행 중·미커밋 작업이 다른 접속으로 넘어가 사고가 나는 경로를 막는다. 세션 선택을 SessionRouter 한 곳에 모아 기능마다 세션을 짐작하던 코드를 없앴다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-20, 「오렌지 처럼 여러 세션 연결을 지원하도록 하자」), multiconn 레인 결정 후보, 조정 세션 확정

## D-033 (2026-10-20T23:50:00Z)
- **Phase**: TSK-04-11 바인드 변수를 SQL 탭마다 따로(EXEC-03)
- **Decision needed**: 바인드 목록을 앱 하나로 둘지 탭마다 둘지, 탭이 없는 도구 창은 무엇을 쓸지
- **Decision made**: 바인드 목록은 SQL 탭마다 하나(SqlEditorViewModel.Binds, BindVariableStore.ForTab)로 둔다. 실행·행 수·그리드 필터·MyBatis 붙여 넣기는 실행한 탭의 목록을 쓴다. 작업 공간·.sql 바인드 블록·.sqd VAR 은 탭별로 저장·복원한다. 탭이 없는 도구 창(객체 상세 데이터 탭)만 For(bus) 기본 목록을 쓴다
- **Rationale**: 두 탭이 같은 이름 :ID 를 다른 값으로 쓸 수 있어야 한다(Orange 와 같음). 탭 접속(D-032)과 같은 「탭 → 그 탭의 것」 자리를 써서 실행 경로가 한 가지 방식만 가진다
- **Reversible**: yes
- **Source**: 사용자 지적(2026-10-20, 「바인드 변수 설정은 SQL 탭별로 있어야 해」), binds 레인 결정 후보, 조정 세션 확정

## D-034 (2026-10-22T03:50:00Z)
- **Phase**: TSK-10-08 서명 없는 dll(TSK-10-06 크기에도 영향, D-025 대체)
- **Decision needed**: 코드 서명 인증서 없이도 Microsoft·Oracle 이 서명한 dll 의 서명을 지키려면 ReadyToRun composite 를 계속 쓸 수 있는지
- **Decision made**: Windows self-contained 게시는 일반 ReadyToRun 으로 바꾼다(PublishReadyToRunComposite=false). 원래 Authenticode 서명이 있는 패키지 dll 11개(Oracle.ManagedDataAccess·SkiaSharp·HarfBuzzSharp·Microsoft.Data.Sqlite·CommunityToolkit.Mvvm·System.* 6개)는 PublishReadyToRunExclude 로 다시 컴파일하지 않는다. composite 를 유지한 채 서명된 dll 만 빼는 방법은 SDK 10.0.402 에서 되지 않는다(PublishReadyToRunExclude 가 무시되고, PublishReadyToRunCompositeExclusions 로 프레임워크를 빼면 crossgen 이 System.Private.CoreLib 를 찾지 못해 실패)
- **Rationale**: 맥 win-x64 게시본 측정(PE 보안 디렉터리): composite 는 exe·dll 254개 중 무서명 236개, 일반 R2R + 제외 목록은 253개 중 51개. 일반 R2R 은 이미 R2R 인 런타임 dll 을 건너뛰어 Microsoft 서명이 남는다. 대가: 입력 가능까지 약 +0.5~0.6 s(추정. D-006 의 1670 → 2276 ms 는 모든 dll 을 R2R 로 컴파일한 일반 R2R 값이고, 서명 dll 11개를 JIT 로 남긴 이 구성은 Windows 에서 재지 않았다). 설치 파일은 99,177,093 → 80,016,283 bytes 로 80 MB 기준(83,886,080) 이하가 된다. 무서명 Oranda.App.exe 와 우리 dll 은 남으므로 스마트 앱 컨트롤·WDAC 가 켜진 PC 는 여전히 서명이 필요하다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-22, 「DLL 서명이 없어도 자연스럽게 실행되도록 하면 된다」, 선택지 「composite 끄고 서명 dll 제외」). D-025 본문은 고치지 않음

## D-035 (2026-10-30T04:20:00Z)
- **Phase**: v0.1.9 릴리스(PRD 7.2 설치 파일 크기)
- **Decision needed**: Windows 설치 파일이 80 MB 기준(83,886,080 bytes) 바로 아래(0.1.8 = 83,048,850 bytes)에 붙어 있어 orandactl 을 설치본에 넣는 것만으로도 넘을 수 있다. 기준을 올릴지, 다른 파일을 더 줄일지
- **Decision made**: 설치 파일 크기 기준을 100 MB(104,857,600 bytes)로 올린다. CI 크기 검사(Windows·macOS)와 check-installer-size.ps1 이 이 값을 쓴다
- **Rationale**: 80 MB 는 기능상 한계가 아니라 배포 목표다. 줄일 수 있는 런타임 파일은 이미 뺐고(TSK-08-07, D-034), 남은 여유로는 다음 기능을 넣을 수 없다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-10-30, 「목표를 100MB로 올려」). D-034 본문은 고치지 않음
