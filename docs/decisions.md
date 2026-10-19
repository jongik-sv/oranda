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
