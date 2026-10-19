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
- **Source**: 커밋 a727e94, e736a79, spikes/TSK-01-03/RESULT.md. 보류: 사내 19c, ADB, macOS ARM

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
