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

## D-036 (2026-11-03T03:00:00Z)
- **Phase**: ai-profiles-image 회차(AI 튜닝 LLM 프로필, 대화 이미지, 일반 대화, 튜닝 레포트, 탭별 계획), v0.1.12
- **Decision needed**: LLM 프로필 경로의 환경 변수·인증·리다이렉트·프록시 정책, 튜닝 레포트 기본 동작, 탭별 실제 계획의 귀속 기준
- **Decision made**: (1) 튜닝 레포트: 가린 대화는 가린 자리표시를 그대로 둔다(되돌리지 않는다). 인덱스 후보는 시작 창에서 고른 때만 넣는다. 파일 이름에 SQL 을 넣지 않는다. (2) 탭별 실제 계획: 세션별 실행 순번(StatementExecuted)과 탭 기록이 같을 때만 `DISPLAY_CURSOR(NULL)` 로 읽고(이 앱의 실행 기록이 없는 세션은 전처럼 읽는다), 그 밖은 안내만 한다. (3) CLI 프로필 경로는 부모 환경에서 Bedrock·Vertex·Foundry 전환, `CUSTOM_HEADERS`, 모델 변수를 물려받지 않는다. Claude Code(구독) 경로는 그대로 둔다. (4) 연결 테스트(Probe)는 실제 실행과 같은 인증 헤더를 프리셋별로 쓴다. (5) 프로필 API 호출은 HTTP 리다이렉트를 따르지 않는다(Probe 와 API 드라이버 공통). 3xx 는 안내로 알린다. (6) 프로필 경로는 프록시를 모두 끈다: CLI 는 프록시 환경 변수를 비우고, `OpenAiApiDriver` 는 `UseProxy=false` 다. 구독 경로는 그대로 둔다
- **Rationale**: 프로필은 내부 네트워크의 서버로 직접 연결하는 것이 목적이다. 주소로 내부·외부를 가르는 판정은 잘못 가르면 연결이 막히므로 만들지 않았다. 부모 환경 변수가 새어 들어가면 프로필 서버가 아닌 곳으로 요청이 나가거나 인증이 어긋난다. 리다이렉트는 키가 든 헤더를 다른 곳으로 보낼 수 있다. 대가: 프록시로만 외부에 나갈 수 있는 환경에서는 클라우드 프로필이 연결되지 않는다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-11-02, 프록시: 「내부 네트워크에서 쓸 수 있으면 된다」), 레인 결정 후보를 조정 세션이 확정. 설계 `docs/superpowers/specs/2026-11-02-ai-image-and-model-profiles-design.md`

## D-040 (2026-11-04T12:00:00Z)
- **Phase**: ai-cli-custom-win144 회차(기타 CLI 등록, opencode 전용 드라이버)
- **Decision needed**: (1) 사용자가 등록하는 기타 CLI 를 어디까지 지원할지, (2) opencode 를 전용 드라이버로 만들었지만 켤지, (3) 명령 틀 표의 「확인됨」 표시를 어떻게 둘지
- **Decision made**: (1) 기타 CLI(`AiCustomCliCommand`)는 일반 대화와 명령 3종(실행 계획 해설, SQL 설명, 오류 고치기)만 돌린다. 튜닝은 시작하지 않고 안내만 하고 닫는다. 가림 대상(운영) 접속에는 보내지 않는다. 자동 승인 옵션이 든 명령은 등록 때 경고한다(막지 않는다). (2) opencode 전용 드라이버(`OpencodeDriver`)는 넣되 프리셋 `opencode-cli` 는 `AiHostPlatform.None` 으로 꺼 둔다. 설정 화면에는 비활성 「opencode」 줄만 있다. 실제 왕복(O3)과 켜기(O4)는 사용자 결정으로 보류한다. (3) 명령 틀 표의 opencode 「확인됨」 표시는 사용자가 유지하기로 했다(근거는 아래 한계). (4) opencode 드라이버 설계: ① MCP 대신 플러그인 직접 도구 경로: MCP 도구는 코드 모드 `execute` 로만 호출되고 그 런타임의 `fetch` 가 외부 HTTP 에 열려 있어(끄는 키 없음) 잠글 수 없다. 플러그인(`plugins/oranda.js`)이 기본 도구를 지우고 앱 도구를 `codemode:false` 직접 도구로 등록하며 브리지에 직접 연결한다(5초 제한도 없음, 200초 호출 정상). ② 검증 버전 고정: 2.0.32 만 허용하고 그 밖은 `cli_version_unverified` 로 거부한다. `tool_use` 가 도구 완료 뒤에만 한 번 나와 스트림 감시가 늦기 때문이다. ③ 이어 말하기: 대화에 저장된 권한이 잠금을 우회한다(대화에 allow `*` 를 저장하고 `--session` 으로 이어 가면 `execute` 가 호출됨, 설정 키·플러그인 훅으로는 막지 못함). 그래서 이어 말하기 전에 `opencode session export --standalone --sanitize` 의 `info` 를 읽어 닫는 쪽으로만 판단한다(`OpencodeSessionCheck`). 남는 틈은 확인과 실행 사이에 다른 쪽이 권한을 저장하는 경우다. ④ 사용자 설정은 `provider`·`model`·`enabled_providers`·`disabled_providers` 네 키만 `OPENCODE_CONFIG_CONTENT` 로 전달한다(설정 파일 링크는 `plugin`·`experimental.policies`·`share` 등 잠금이 다루지 않는 키가 살아나 폐기). 읽지 못하거나 모양이 다르면 `opencode_config_unsupported`. 값 안에 주석이 있는 JSONC 도 읽는다
- **Rationale**: 기타 CLI 는 도구 잠금을 Oranda 가 걸 수 없으므로 DB 도구를 쓰는 튜닝과 가림 대상 접속을 열지 않는다. opencode 는 드라이버가 가짜 LLM 측정으로만 확인됐고 D-037 의 켜기 조건(플러그인 도구 호출 성공, 구조 결과 파싱, 스트림 감시, 잠금 쪽 실행 뒤 검사)을 실제 모델 왕복으로 확인하지 못했다. 왕복 4차 중 3차는 모델 호출 전에 닫혔다(무료 한도, 사용자 공급자 소실, JSONC 주석 파싱 오류). 마지막은 보류
- **Limits**: ① 사용자 설정 `provider` 블록에 키가 글자 그대로 있으면 `OPENCODE_CONFIG_CONTENT` 환경 변수로 opencode 자식 프로세스(`opencode serve --stdio` 포함)와 그 플러그인 코드에 전달된다. 독립 리뷰 대상이며 켜기 전에 확인한다. ② 사용자 전역 플러그인 코드는 돈다(조정 결정: 허용). ③ 대화·도구 결과가 사용자 전역 opencode 저장소에 남는다. ④ 도구 카드는 완료 때만 보인다. ⑤ macOS 관리 폴더·MDM·원격 설정의 MCP 는 덮어쓰기에 안 잡힐 수 있다(추정). ⑥ 사용자 설정에 remote MCP 가 있으면 사전 거부한다(과잉일 수 있음). ⑦ 모델을 지정하지 않으면 opencode 기본 모델로 가고, 없으면 무료 한도 오류 원문이 그대로 나온다. ⑧ 기타 CLI: 가림은 환경 변수 값과 글자 그대로 같은 글만 해당하고, 명령 인자에 직접 쓴 비밀은 가리지 않는다. 5글자 미만·`true`/`false` 류·숫자만인 값은 가림 대상에서 빠져 짧은 비밀이 답에 보일 수 있다. 앱을 닫아도 실행 중인 CLI 가 바로 멈추지 않을 수 있다(코드로 확인하지 못함). 설치 표시(PATH 기준)와 실제 실행 결과가 다를 수 있다. ⑨ 명령 틀 표의 opencode 「확인됨」 은 Mac 조정 메모의 기록을 인용한 것이다(원 출력 없음). 같은 날 Mac 재시도는 무료 사용량 한도, win144 1회는 CLI 안 오류로 응답을 받지 못했다. Codex 「확인됨」 은 Mac·win144 에서 직접 왕복했다
- **Reversible**: yes(opencode 는 프리셋 `EnabledOn` 값 하나, 기타 CLI 는 설정 화면의 등록 목록)
- **Source**: 사용자 결정(opencode 왕복·켜기 보류, 「확인됨」 표시 유지), 조정 세션 확정(2026-11-04). D-037, D-038, D-039 본문은 고치지 않음

## D-039 (2026-11-04T04:40:00Z)
- **Phase**: 0.1.14 준비
- **Decision needed**: D-038 (2)의 시스템 CLI 운영 접속 차단을 유지할지
- **Decision made**: 차단을 해제한다. `RoutingAiDriver` 는 운영 접속의 요청도 Codex·Antigravity 드라이버로 보내며 `system_cli_production_blocked` 오류 코드는 없앤다. 설정 고지의 「운영 접속에서는 DB 도구를 쓰는 요청을 보내지 않습니다.」 문장은 운영 접속에서도 쓸 수 있고 가린 뒤의 대화 내용도 CLI 폴더에 남는다는 안내로 바꾼다.
- **Rationale**: 사용자가 Claude 외의 도구에만 걸린 차단을 풀어 달라고 요청했다. 실제 왕복과 Windows 잠금(실행 토큰의 프로세스 환경 노출, 파일 권한)은 여전히 확인하지 못했으므로 이 위험은 남는다. 차단이 없어도 `AgentGate`, `AiAccessPolicy`(운영 기본 허용 수준), 운영 리터럴 가림, 대화 흐름의 확인 창은 시스템 CLI 경로에도 그대로 적용된다.
- **Reversible**: yes(조건 하나를 되돌리면 된다)
- **Source**: 사용자 요청(2026-11-04, 「Claude를 제외한 다른 도구들은 운영계 접속 차단이 되어 있는데 풀어줘.」). D-038 본문은 고치지 않는다.

## D-038 (2026-11-04T09:00:00Z)
- **Phase**: 0.1.13 준비
- **Decision needed**: D-037 에서 끈 Codex·Antigravity 를 실제 왕복 전에 켤지
- **Decision made**: (1) 사용자 요청으로 두 CLI 를 모든 플랫폼에서 고를 수 있게 켜고, 시험은 사용자가 릴리스 뒤 직접 한다. (2) `RoutingAiDriver` 가 운영 접속의 DB 도구 요청을 `system_cli_production_blocked` 로 막는다. 사용자는 켜 달라고 했을 뿐 이 차단을 고르지 않았으며, 범위를 고르는 질문에 답이 없어 구현한 세션이 더 안전한 쪽으로 정했다. (3) AI 가 쓰는 조인 문법을 설정(`AiSettings.JoinSyntax`, 기본 Oracle, ANSI 선택)으로 바꾼다
- **Rationale**: 드라이버 코드는 들어와 있고 사용자가 시험을 맡았다. 다만 왕복과 Windows 잠금(실행 토큰의 프로세스 환경 노출, 파일 권한)이 확인되지 않아 운영 DB 도구까지 열지는 않는다
- **Reversible**: yes(차단은 조건 하나, 켜기는 `EnabledOn` 값이다)
- **Source**: 사용자 요청(2026-11-04, 「Codex CLI·Antigravity CLI 을 선택하도록 하자」, 「테스트는 릴리즈후에 해볼께」)

## D-037 (2026-11-03T09:00:00Z)
- **Phase**: ai-cli-commands 회차(AI 명령 3종, 운영 벤치마크 제한 시간, Oracle 조인 문법 규칙, 시스템 CLI 전달)
- **Decision needed**: 시스템 CLI(Codex·Antigravity)를 이번 회차에 켤지, 켠다면 어느 플랫폼에서 켤지
- **Decision made**: (1) 두 CLI 모두 `AiHostPlatform.None`(「준비 중」)으로 머지하고 어떤 플랫폼에서도 켜지 않는다. 설정 파일에 CLI 가 저장되어 있어도 프로세스를 띄우지 않는 것을 시험으로 고정한다. (2) 켜는 절차(아래)는 실제 왕복을 Mac 에서 CLI 당 1회 돌린 뒤 통과한 CLI 만 켠다. 실패한 CLI 는 `None` 으로 둔다. 한쪽 실패가 다른 쪽을 막지 않고, 다시 불합격이면 추가 실행 없이 보고한다. (3) 잠금 판단은 macOS 한정이다. Windows 에서 켜려면 잠금 측정을 전부 다시 해야 하고, 토큰을 프로세스 환경에서 제거하는 일이 먼저다. (4) 앱 확인은 기존 개발·테스트 접속으로 하고 운영 접속은 쓰지 않는다. (5) 오류 위치 단위는 바이트로 확인했다(AL32UTF8 개발 DB 에서 `SELECT '가나다' AS a, zz FROM dual`: `zz` 는 열 20 인데 오류로 이동한 커서와 오류 창이 열 26). 이번에는 고치지 않고 후속 `ToDbError` 로 확정하며, 사용자 문서에 한계로 적는다
- **Rationale**: 두 드라이버는 실제 로그인으로 한 번도 끝까지 돌지 않았다(Codex 는 스키마 없는 스크립트 왕복의 통과 판정을 철회했고, agy 는 `step_type: finish` 를 감시가 닫아 도구 호출 0건으로 불합격한 뒤 고쳤으나 다시 돌리지 않았다). 왕복 시험은 Windows 에서 건너뛰므로 이 PC 에서는 켜기 조건을 확인할 수 없다. 검증하지 않은 기능이 켜진 채 나가는 것을 막는다
- **Reversible**: yes
- **Source**: 사용자 결정(2026-11-03, Codex·agy 둘 다 준비 중으로 머지, 앱 확인은 개발·테스트 접속). 상위 조정자가 권한 다른 안(왕복만 Mac 에서 하고 결과에 따라 켜기)은 채택하지 않았다

### 켜는 절차

실제 로그인, 실제 드라이버 코드, 실제 `TuningResult` 스키마로 도는 시험이 다음을 모두 만족해야 켠다.

1. MCP 도구 호출이 1건 이상 성공한다.
2. 최종 결과가 `AiTuningFlow.Capture` 로 파싱된다(구조 결과가 비어 있지 않음).
3. 스트림 감시가 정상 실행을 닫지 않는다.
4. 잠금 쪽 확인(토큰, 자식 프로세스, 도구 목록)이 그대로 통과한다. 시험은 이를 단언하지 않으므로 사람이 본다: 실행 중 프로세스 환경의 `ORANDA_RUN_TOKEN` 값이 `~/.codex`, `~/.gemini/antigravity-cli`, 임시 폴더의 새 파일에 남지 않았는지(값을 파일에 남기지 않는다), `orandactl` 외 자식 프로세스와 실행 뒤 잔존 프로세스, 모델이 부른 도구가 `AgentTools` 안에 있는지(Codex 는 새 세션 기록, agy 의 `init.tools` 는 잠금을 반영하지 않으므로 쓰지 않는다).

명령: orandactl 을 먼저 Debug 빌드하고 `ORANDA_AI_E2E_AGY_REAL=1 dotnet test tests/Oranda.App.Tests -c Debug --filter FullyQualifiedName~AiSystemCliAgyRoundTripTests`, `ORANDA_AI_E2E_CODEX_REAL=1 ... ~AiSystemCliCodexRoundTripTests`. 검증한 버전은 agy 1.4.12, Codex 0.166.0 이다. 켠 뒤에는 TRD 4.10 의 「켤 때 쓸 문안」 을 사용자 문서에 옮긴다.

### 잠금 판단

모든 잠금 측정은 macOS 에서만 했고 판정도 macOS 한정이다. Windows 에서 다시 볼 것: 프로세스 환경을 다른 프로세스가 읽는 경로와 자식 상속, 폴더·파일 권한(ACL), `USERPROFILE`·`CODEX_HOME`, 에이전트 파일의 역슬래시·한글 경로, `.cmd`·`.bat` 거부 규칙, 명명된 파이프, Job Object 와 종료 지연, 설정 탐색 규칙과 샌드박스, 갱신기, 개발 빌드에서 MCP 자식 환경이 비어 .NET 을 못 찾는 문제(제품 `env_vars` 에 `DOTNET_ROOT` 를 더하지 않는다). agy 전역 MCP 서버가 있으면 거부하고(`mcp_config_present`, 대화 DB 에 환경이 통째로 저장됨), 사이드카가 있으면 거부한다(`sidecar_config_present`).

### 취소 전달 재측정 표 (실제 CLI, `orandactl`, `AgentBridgeHost`, 대체 게이트, macOS)

| 경로 | 브리지 `ct` | 처리자 토큰, agy | 처리자 토큰, Codex |
|---|---|---|---|
| 사용자 중지 | 취소 안 됨 | 8.04초(중지 즉시) | 3.0초(중지 즉시) |
| 실행 제한 초과 | 취소 안 됨 | 7.77초 | 5.8초 |
| CLI 가 호출만 포기, 실행 계속 | 취소 안 됨 | 10.06초(실행 끝, 포기 5초에는 안 됨) | 4.1초(실행 끝) |

- 측정은 토큰이 취소되는 시각까지다. 실제 `AgentGate` 와 DB 의 반응은 재지 않았다. **DB 반응은 앱 확인 결과를 따른다.** 게시본(Windows)·개발 DB·Claude Code 경로에서 본 것: 벤치마크 실행 중 중지하면 DB 활성 문장이 약 5초 안에 사라지고 `claude` 가 끝나며 감사 행에 `run_revoked` 가 남는다. 제한 시간 5초로 두면 해당 벤치마크 호출이 5.3초에 `tool_failed` 로 끝나고 문장이 사라진다. 운영 표시 확인은 같은 DB 를 가리키는 격리 임시 프로필로 했다. **Codex·agy 왕복 경로는 미검증이다.**
- 앞뒤가 맞지 않는 측정 1건: agy 는 포기 뒤 다음 호출이 `_callGate` 에 막혀 도착 0건이었고, Codex 는 포기 뒤 두 번째 호출이 게이트에 들어왔다. 원인은 확인하지 못했다.
- 남는 빈틈: 「CLI 가 호출만 포기하고 실행은 계속」, 「`orandactl` 만 죽고 실행은 계속」. 연결 끊김 감시는 후속이다.

### 앱 확인 (게시본, Windows, 개발 DB)

확인함: Claude Code 튜닝 시작과 끝, 튜닝 재작성이 ANSI 입력에도 `(+)` 로 나옴(상수 조건 `(+)` 포함), 오류 고치기의 새 탭 「(수정)」과 오타 수정의 ANSI 유지, 접속을 바꾸면 오류 고치기 메뉴 비활성, 실행 계획 해설이 「실행하고 실제 계획」(힌트 사본)의 계획도 실제 계획으로 읽음, `orandactl` → `AgentGate` → `agent_audit` 행, Codex·agy 「준비 중」 비활성과 `codex-cli` 가 저장돼 있어도 프로세스 없이 안내로 닫힘, 벤치마크 시작 창 기본값 = 설정값·범위 밖 입력은 시작 불가·0 은 경고. 못 본 것: Codex·agy 실제 왕복.

### 한계로 남긴 것
- 오류 위치(열)가 바이트 단위라 오류 앞에 멀티바이트 글자가 있으면 실제보다 뒤로 표시된다.

- 거부 판정표: 이어 말하기 차례는 안내 없이 통과한다(레포트에 섞이므로).
- agy 프롬프트가 도구 목록으로 약 5.6K자 늘고 대화 DB 에 영구 저장되므로 비밀을 넣지 않는다(시험 있음).
- 미검증: 프로젝트 `.codex/config.toml` 대조군, 앱 커넥터 대조군, 플러그인 미끼 E2E, 원격 번들, 관리형 훅, 다른 Codex 버전.

### 후속 목록

1. 실제 왕복과 켜기(Mac 에서 CLI 당 1회).
2. `AiTuningLauncher.UnavailableReason`.
3. 토큰을 프로세스 환경에서 제거(파일로 넘기기). Windows 에서 켜기 전 필수.
4. Windows CLI 재시험(위 잠금 판단 목록 전부).
5. 연결 끊김 감시(`AgentBridgeHost`). 지금은 Claude·API 경로 불변을 위해 고치지 않았다.
6. `agent_audit` 에 적용된 제한 시간 기록(`AgentGate` 의 감사 항목 구성 수정, 스키마 변경 없음).
7. 튜닝 대화의 거부 문구.
8. `ToDbError`: 오류 위치를 바이트가 아닌 문자 단위로 바꾼다(`Oranda.Oracle`). 바이트 단위로 확인되어 확정 항목이다.

## D-041 (2026-11-05T09:00:00Z)
- **Phase**: editor-column-edit 회차(editor-multicaret 레인: 세로 다중 커서와 열 블록 선택)
- **Decision needed**: D-007 이 다중 커서 1.0 범위로 정한 것(입력·삭제·Enter·Alt+클릭·Ctrl+Alt+↑/↓·AvaloniaEdit 내장 열 선택)을 어디까지 넓힐지, 포크 없이 가능한 범위는 어디까지인지
- **Decision made**: 사용자가 정한 동작을 포크 없이 `MultiCaretController`(partial 5개) 한 곳에서 구현한다. ① 이동: 방향키·Home·End·단어 이동이 모든 커서를 함께 옮기고, Shift 를 함께 누르면 커서마다 따로 선택한다. AvaloniaEdit 의 이동 계산이 internal 이라 직접 구현했고, 단어 경계는 `TextUtilities.GetNextCaretPosition` 으로 구한다. 세로 이동은 글자 열을 목표 열로 기억한다. ② 클릭: 일반 왼쪽 클릭은 추가 커서를 해제하고, 컨트롤러 밖에서 캐럿이 움직이면(PageUp·찾기 이동·줄 이동·실행 취소) 추가 커서도 해제한다. ③ Alt(macOS Option) 왼쪽 버튼은 제자리에서 떼면 커서 추가, 끌면 열 블록 선택이다. 내장 사각 선택은 수식키가 OS 마다 달라(macOS 는 Control) Windows 와 macOS 조작을 같게 하려고 쓰지 않고 `RectangleSelection` 을 직접 만든다. macOS 내장 Control+드래그는 건드리지 않는다. 커서와 열 블록은 동시에 두지 않는다(끌기를 시작하면 커서를 지우고, 커서를 더하면 사각 선택을 지운다). ④ 커서별 선택: 추가 커서마다 선택 앵커를 두고 겹치는 선택은 합친다(주 커서 포함). 입력·Backspace·Delete·Enter 는 선택이 있으면 선택을 바꾸고 한 번의 실행 취소 단계다. ⑤ 클립보드: 복사·잘라내기는 선택을 가진 커서의 글을 문서 순서로 줄바꿈으로 이어 넣고(선택 없는 추가 커서만 있으면 꺼 둔다), 붙여넣기는 클립보드 줄 수와 커서 수가 같으면 커서마다 한 줄씩, 다르면 전체를 각 커서에 넣는다. 열 블록으로 복사한 글은 응용 형식 표지(`oranda-block-text`)와 프로세스 안 마지막 블록 글 비교로 알아보고 `RectangleSelection.PerformRectangularPaste` 로 블록 모양으로 붙인다. ⑥ Tab 은 각 커서·블록 줄의 열 위치에 들여쓰기 글자를 넣고, Shift+Tab 은 해당 줄을 내어쓴다. ⑦ 실행·글자 연산: 열 블록과 선택을 가진 여러 커서는 줄(커서)마다 선택된 글 조각(`SqlEditorViewModel.SelectionPieces`)을 낸다. 선택 실행은 조각만 줄바꿈으로 이어 실행하므로 블록 밖 SQL 은 실행되지 않는다. 문장 범위·오류 위치·실행 범위 강조는 조각별로 문서에 대응시킨다. 대소문자 변환 같은 글자 연산은 조각에만 적용하고 한 번의 실행 취소 단계로 묶는다. 줄 단위 명령(주석·들여쓰기·줄 복제·줄 삭제)은 블록이 걸친 줄 전체에 적용하는 지금 동작을 유지한다
- **Rationale**: 모두 공개 확장점(`TextAnchor`·`IBackgroundRenderer`·터널 이벤트·`RectangleSelection`·`TextUtilities`)으로 되고 AvaloniaEdit 를 포크하지 않으므로 D-007 의 「포크 없음」 방침은 유지한다. 범위만 D-007 을 넘는다. 열 블록 선택의 `SelectionStart`·`SelectionLength` 는 블록을 감싸는 한 덩어리(블록 밖 글 포함)여서, 이를 그대로 쓰던 선택 실행이 블록 밖 SQL 까지 실행하는 결함이 있었다. 그 결함을 시험으로 먼저 고정한 뒤 고쳤다
- **Limits**: ① 세로 이동의 목표 열은 시각 X 가 아니라 글자 열이다(고정폭 글꼴 전제, 탭 문자가 섞인 줄은 어긋날 수 있음). ② 한글 조합 중 입력은 AvaloniaEdit `SupportsPreedit` 가 false 라 시험하지 못했다(확정 글자만 시험). ③ 자동 완성 창처럼 입력을 쥐는 처리기가 열려 있으면 컨트롤러는 키를 넘긴다. ④ `SelectionStart`·`SelectionLength` 는 열 블록에서 블록을 감싸는 범위라, 이를 한 덩어리로 읽거나 바꾸던 다른 기능(선택 영역 포맷, 코드 조각 삽입, MyBatis 로그 붙여넣기, 스키마 브라우저 삽입, 확장 시험, 표 배치, AI 대화 문맥)은 조각이 둘 이상인 선택에서 하지 않고 알리거나(`SqlEditorViewModel.BlockSelectionNotice`) 선택 없음으로 본다. 컴파일 오류 이동(`SqlCompileErrors`)은 조각을 따라 블록 안 글자로 간다. 탭 복제와 View 를 새로 만들 때의 선택 복원도 둘레 범위를 되살리지 않는다(블록이면 캐럿만). 순차 실행·결과 편집 모드 실행·실행 계획은 일반 선택과 같게 캐럿의 문장 전체를 쓴다. ⑤ 사각 선택 끌기는 Alt+누른 자리에서 포인터 캡처를 잡고 놓기·버튼 없음 검사로 끝낸다. 편집기 위·아래 밖으로 끌면 보이는 첫·끝 줄에 고정하고 자동 스크롤은 하지 않는다. ⑥ 커서 이동·삭제는 CRLF 를 한 경계로 건너뛴다(캐럿은 CR 과 LF 사이에 놓일 수 없다). 블록 모양 붙여넣기는 문서 끝에 줄이 모자라면 줄을 더하고, 일반 선택이 있으면 선택을 대체한다. 문장 글의 조각 대응은 전역 표가 아니라 `SqlStatement.SourcePieces`(소유 밖 Oranda.Core 에 속성 하나 추가)에 실어 `with` 복사에도 따라간다
- **Reversible**: yes(컨트롤러 안 동작이라 되돌려도 문서 형식은 달라지지 않는다)
- **Source**: 사용자 결정(editor-column-edit 회차), 조정 세션 기본안. D-007 본문은 고치지 않음

## D-042 (2026-11-05T12:00:00Z)
- **Phase**: editor-column-edit 회차(editor-preedit 레인: macOS 한글 조합 글자 표시)
- **Decision needed**: macOS 에서 한글을 조합하는 동안 글자가 편집기에 보이지 않는 증상(초성·중성·종성을 쳐도 다음 글자나 Enter·Space 로 확정해야 보임)을 AvaloniaEdit 포크 없이 고칠 수 있는지, 고친다면 어떻게 보여 줄지
- **Decision made**: 사용자가 이번에 고치기로 했다. 포크하지 않고 앱에서 입력기 클라이언트를 바꾼다. ① AvaloniaEdit `TextArea` 의 클라이언트는 private 이고 `SupportsPreedit` 가 false 로 고정이라 조합 글자(`SetPreeditText`)를 받지 못한다. `SqlEditorView` 가 `TextInputMethodClientRequested` 이벤트에 버블 단계 인스턴스 처리기를 달아, AvaloniaEdit 클래스 처리기가 세운 `e.Client` 를 `PreeditInputMethodClient`(위임 클래스)로 감싸 바꾼다. 캐럿 사각형·주변 글·선택·이벤트는 안쪽 클라이언트에 위임하므로 AvaloniaEdit 와 같은 값을 내고, 바뀌는 것은 `SupportsPreedit`(true)와 `SetPreeditText` 둘뿐이다. 읽기 전용 편집기는 AvaloniaEdit 가 클라이언트를 주지 않으므로(null) 그대로 둔다. ② 표시는 `VisualLineElementGenerator` 로 캐럿 자리에 문서 길이 0 인 시각 요소(`PreeditElement`)를 끼워 편집기 글꼴과 밑줄로 그린다. 뒤 글자는 오른쪽으로 밀려 겹치지 않고, 스크롤·글꼴 크기·긴 줄·접기는 AvaloniaEdit 가 처리한다. 문서·실행 취소·`IsDirty`·파서·자동 완성·바인드 스캔은 조합 글자를 보지 못한다. 추가 커서는 커서마다, 열 블록은 줄마다 블록 왼쪽 끝(`RectangleSelection.Segments` 의 시작)에 같은 글자를 그린다. 폭 있는 블록의 글자는 확정 전까지 지우지 않는다. ③ 조합 글자는 `TextAnchor` 로 자리를 잡고, 빈 문자열·확정 `TextInput`(문서에 글자가 들어가기 전 터널 단계)·Esc·포커스 이탈·포인터 누름·캐럿 이동·문서 교체·화면에서 떨어짐에서 지운다. ④ `OperatingSystem.IsMacOS()` 일 때만 클라이언트를 바꾼다(`SqlEditorView.PreeditEnabled`). 다른 플랫폼은 이전과 같다
- **Rationale**: D-007 의 「포크 없음」 방침을 지킨다. 공개 API(`TextInputMethodClient` 의 가상 멤버, 라우팅 이벤트, `VisualLineElementGenerator`)만 쓰고 AvaloniaEdit private 멤버를 리플렉션으로 건드리지 않는다. 글자 배경을 칠하는 덧그리기 대신 시각 요소를 끼우는 방식을 고른 것은 뒤 글자와 겹치지 않고 레이아웃 계산을 AvaloniaEdit 에 맡길 수 있기 때문이다
- **Limits**: ① 헤드리스는 실제 입력기를 재현하지 못해 시험은 클라이언트 메서드를 직접 불러 확인했다. 실제 macOS 키보드 확인은 게시 빌드로 해야 한다(Debug 빌드는 조합 동작이 다르다). ② 확정 때 입력기가 `insertText`(TextInput) 다음에 `setMarkedText`(SetPreeditText) 순서로 부른다고 가정한다(Cocoa 표준 순서). 반대 순서면 새 조합 글자가 확정 직후 지워질 수 있다. ③ 확정 글자가 오지 않고 `SetPreeditText("")` 도 오지 않는 취소는 Esc·포커스 이탈·포인터·캐럿 이동으로만 지운다. ④ 조합 중 캐럿은 조합 글자 앞에 그려지고, 입력기 후보 창 위치는 캐럿 사각형을 그대로 쓴다. ⑤ 조합 중 Backspace 는 입력기가 처리한다. 앱은 조합을 지우는 키 처리를 더하지 않는다. ⑥ Windows(MS IME)는 확인하지 않았으므로 바꾸지 않았다. ⑦ 생성기는 맨 앞에 두어(접기 관리자가 View 를 붙일 때마다 맨 앞에 다시 넣으므로 조합 글자를 올릴 때마다 확인) 링크·접기 요소가 시작하는 자리에서도 보인다. 다만 캐럿이 링크·접힌 구간 안쪽에 있거나 3000자가 넘는 줄(AvaloniaEdit 가 생성기를 부르지 않음)이면 확정 전까지 보이지 않는다. ⑧ 줄 끝을 넘겨 끈 열 블록에서 짧은 줄의 조합 글자는 줄 끝에 그려지고, 확정하면 공백이 채워져 블록 열로 옮겨진다
- **Reversible**: yes(처리기 등록 한 줄과 새 파일 3개를 빼면 이전 동작으로 돌아가고 문서 형식은 달라지지 않는다)
- **Source**: 사용자 결정(editor-column-edit 회차, 설치본 0.1.14 에서도 같은 증상), 조정 세션 지시 editor-preedit-1

## D-043 (2026-11-05T12:00:00Z)
- **Phase**: editor-column-edit 회차(grid-sort-menu 레인: 결과 그리드 정렬 메뉴 최상위)
- **Decision needed**: 정렬 3항목(오름차순·내림차순·정렬 해제)을 「열」 하위에서 최상위로 올리면 그리드 오른쪽 클릭과 결과 메뉴의 한 단계 줄 수가 15를 넘는데, PRD 13.5·D-023 상한(15줄)을 어떻게 할지
- **Decision made**: 정렬 3항목을 구분선으로 묶어 「거르기」 바로 위 최상위에 둔다. 결과 메뉴는 오른쪽 클릭 메뉴를 그대로 미러링하므로 함께 바뀐다. 결과·그리드 오른쪽 클릭 한 단계 상한은 18줄로 완화하고, 15줄을 넘는 메뉴 수 상한은 모든 기능 6, R1 2(그대로), R6 2로 고친다. 다른 항목은 옮기지 않는다. 편집·파일 15 이하, 보기 7 이하는 그대로다.
- **Rationale**: 정렬을 한 번에 쓰게 하려는 사용자 요청이 줄 수 상한보다 우선한다. 명령 id·단축키·제목·켜짐 조건은 그대로다.
- **Reversible**: yes(배치 3줄과 `ContextSectionOrder` 의 "sort" 한 단어, 시험 상한 숫자를 되돌리면 된다)
- **Source**: 사용자 결정 2026-11-05, 조정 세션 전달. 관련: D-023(docs/oranda1)

## D-044 (2026-11-05T15:00:00Z)
- **Phase**: 회차 밖(MyBatis 바인드 값 저장)
- **Decision needed**: 시나리오가 없는 쿼리의 바인드 값은 메모리에만 있어 매퍼를 닫으면 사라진다. 매퍼 파일을 건드리지 않고 어디에 남길지
- **Decision made**: 시나리오가 없는 쿼리에 사용자가 값을 입력하면 지금 값으로 「자동 저장」 시나리오를 만들어 고른다. 그 뒤로는 다른 시나리오처럼 고칠 때마다 저장하고, 이름을 바꾸거나 지울 수 있다. 새 표를 만들지 않고 로컬 `oranda.db` 의 `mybatis_scenario` 를 그대로 쓴다. 비밀 이름 값을 저장하지 않는 규칙(MB-04)은 바꾸지 않고, 고른 형식은 저장하지 않는다.
- **Rationale**: 저장 위치, 매퍼를 옮겼을 때의 추적, 다시 열 때의 복원이 시나리오에 이미 있어 저장 방식을 둘로 늘리지 않아도 된다. 무엇이 저장됐는지 시나리오 목록에 보이고, 사용자가 지울 수 있다.
- **Reversible**: yes(`MyBatisStatementEval` 의 자동 만들기를 빼면 예전대로 돌아간다. 이미 만든 「자동 저장」 시나리오는 보통 시나리오로 남는다)
- **Source**: 사용자 요청 2026-11-05 「마이바티스 파일도 로컬 어딘가 바인드 변수가 저장이 되면 좋겠는데」
