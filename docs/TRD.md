# Oranda TRD — 기술 설계

| 항목 | 내용 |
|---|---|
| 문서 상태 | 초안 v0.1 (2026-10-19) |
| 상위 문서 | `docs/PRD.md` (요구사항 ID는 PRD 기준: CONN, EDIT, FMT, EXEC, GRID, PLAN, SCH, PLSQL, SESS, SQLM, SAFE, EXP, APP) |
| 참고 | `docs/orange-analysis.md`(Orange 구조·SQL 현대화표), 컴포넌트 조사(2026-10, NuGet·GitHub 직접 조회) |
| 대상 | Windows 10/11 x64, macOS 14.7+ ARM64 / Oracle 19c, 23ai·26ai, ADB |

---

## 1. 설계 원칙
1. **UI 스레드에서 DB·파일·파싱 작업을 하지 않는다.** 모든 장시간 작업은 `CancellationToken`을 받는다.
2. **화면은 Oracle을 모른다.** UI → 서비스 인터페이스 → Oracle 구현. 드라이버를 인터페이스 뒤에 둬서 나중에 구버전 서버(11.2·12c·18c, PRD D-06)용 드라이버를 붙일 수 있게 한다.
3. **딕셔너리 SQL은 코드에 흩어 두지 않는다.** 버전·권한·라이선스 태그가 붙은 SQL 카탈로그(4.2)에서만 꺼낸다.
4. **모든 SQL 실행은 정책 관문을 지난다.** 사용자가 쓴 SQL, 그리드 편집이 만든 DML, 나중에 AI가 만든 SQL 모두 같은 관문(4.9)을 통과한다.
5. **서버에 아무것도 설치하지 않는다**(PRD C-05).
6. **필요할 때 로드한다.** 메인 창을 먼저 띄우고 파서 워밍업·문법 로드·딕셔너리 캐시는 뒤에서 한다(8장).

---

## 2. 기술 스택

### 2.1 런타임 결정 (T-D01, 결정됨: A안 — 2026-10-19)
**.NET 8 LTS 지원이 2026-11-10에 끝난다.** 이후 보안 패치가 없다. 주요 의존 라이브러리(Dock, AvaloniaEdit, Avalonia.Headless.XUnit v3)의 새 개발도 Avalonia 12에서만 진행된다.

| 안 | 내용 | 장점 | 단점 |
|---|---|---|---|
| **A (권장)** | **.NET 10 SDK·런타임(LTS, 2028-11까지) + Avalonia 12** | 지원 기간 확보, 최신 라이브러리 사용. 지금은 코드가 거의 없어 전환 비용이 가장 작다 | .NET 10 SDK 설치 필요. 아래 표의 버전을 12 계열로 다시 고정해야 한다 |
| B | .NET 10 런타임 + Avalonia 11.3 유지 | UI 라이브러리 변경 없음 | 11.3 계열 라이브러리는 유지보수만 받는다 |
| C | .NET 8 유지 | 변경 없음 | 3주 뒤 지원 종료. 사내 보안 기준에 걸릴 수 있다 |

**A안으로 결정했다(2026-10-19).** .NET 10 SDK 10.0.402를 설치하고 .NET 8 SDK는 삭제했다. 프로젝트 대상은 `net10.0`, Avalonia는 12.1.4다. 아래 2.2 표는 이 기준이다.

### 2.2 구성 요소 (.NET 10 + Avalonia 12 기준)
| 영역 | 선택 | 버전 | 라이선스 | 선택 이유 / 비고 |
|---|---|---|---|---|
| 런타임 | .NET | 10 (SDK 10.0.402) | MIT | LTS, 2028-11까지 지원 |
| UI | Avalonia | 12.1.4 | MIT | Windows·macOS 같은 코드, XAML MVVM. 개발자 도구는 AvaloniaUI.DiagnosticsSupport 2.2.3(디버그 빌드만) |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 | MIT | 소스 생성기. `[ObservableProperty]`는 필드 방식 사용 |
| 호스팅·DI·설정·로깅 추상화 | Microsoft.Extensions.* | 10.0.x | MIT | Generic Host를 `Program.Main`에서 만들고 `App`에 ServiceProvider 전달 |
| 로깅 | Serilog + Sinks.File | 4.4.0 / 7.0.0 | Apache-2.0 | 코드 기반 설정(리플렉션 설정 미사용 — 트리밍 대비) |
| DB 드라이버 | Oracle.ManagedDataAccess.Core | 23.26.301 | Oracle Free Use Terms | Thin, 19c 이상, 23부터 실제 비동기. 파이프라이닝은 끈다(취소·타임아웃 미지원). 딸려 오는 `System.Security.Cryptography.Pkcs` 8.0.1에 알려진 취약점이 있어 10.0.13을 직접 참조한다 |
| 에디터 | Avalonia.AvaloniaEdit (+ AvaloniaEdit.TextMate) | 12.0.0 | MIT | 문서 rope·줄 가상화, 접기, 자동완성 창, 열 선택. **다중 커서 정식 지원 없음** → EDIT-07은 자체 확장 또는 단계 조정(T-D03) |
| 도킹 | Dock.Avalonia | 12.1.0.12 | MIT | 사실상 유일한 OSS 도킹. 레이아웃 직렬화는 앱 자체 DTO로 감싼다 |
| 결과 그리드 | **자체 구현 가상 그리드** | — | — | 100만 행·넓은 테이블 대응(4.4). 보조 표(속성 창 등)는 Avalonia.Controls.DataGrid 12.1.2 |
| 파서 | 자체 렉서 + 경량 하향식 파서 / Antlr4.Runtime.Standard + grammars-v4 PL/SQL | 4.13.1 | BSD-3 / MIT | 2계층(4.5). ANTLR은 분석·포매터용으로 백그라운드에서 |
| Excel | MiniExcel | 1.46.0 (1.x 고정) | Apache-2.0 | DataReader에서 바로 스트리밍. 1,048,576행 초과 시 시트 분할 |
| 로컬 DB | Microsoft.Data.Sqlite | 10.0.x | MIT | 실행 이력·메타데이터 캐시(5.3) |
| 자격 증명 | DPAPI(`ProtectedData`) / macOS 키체인 P/Invoke | 10.0.x / — | MIT | `ICredentialStore`로 추상화 |
| 업데이트·패키징 | Velopack (`vpk`) | 1.2.161 | MIT | Windows 설치 파일·델타 업데이트, macOS .app/.pkg 서명·공증. **macOS 패키지는 Mac에서만 생성** |
| 테스트 | xunit.v3 4.0.2, Avalonia.Headless.XUnit 12.1.4, Testcontainers.Oracle 4.16.0 | — | Apache-2.0 / MIT | Avalonia 12의 Headless 테스트는 xunit v3를 지원한다. 기존 Core 테스트(xunit v2)는 M0 솔루션 재구성 때 v3로 옮긴다 |

### 2.3 쓰지 않기로 한 것
| 후보 | 이유 |
|---|---|
| Avalonia.Controls.TreeDataGrid | 11.2.0(2025-10)부터 유료(Avalonia Accelerate) 라이선스 키 필요 |
| Monaco 에디터 + WebView | Avalonia WebView는 유료, CefGlue는 용량·시작 비용 큼 |
| Tree-sitter | 쓸 만한 PL/SQL 문법 없음, 네이티브 바이너리 배포 부담 |
| ClosedXML | 전체를 메모리에 올림(100만 행이면 GB 단위) |
| Devlooped.CredentialManager, KeySharp | 바이너리 사용 라이선스 조건 / 2022년 이후 방치 |
| NativeAOT (1.0) | ODP.NET·Dock·AvaloniaEdit의 AOT 호환이 확인되지 않음. ReadyToRun으로 시작하고 M0에서 측정 후 재판단 |

---

## 3. 아키텍처

### 3.1 계층
```
┌──────────────────────────── Oranda.App (Avalonia) ─────────────────────────────┐
│ Shell(도킹·명령 팔레트·키맵·테마)  도구 창: SQL 편집기 · 결과 그리드 · 스키마 브라우저 │
│ · PL/SQL · 세션 모니터 · SQL 모니터 · 실행 계획      ViewModels (CommunityToolkit)  │
└──────────────┬──────────────────────────────┬───────────────────────────────────┘
               │ 서비스 인터페이스 (6장)        │ 확장 지점 (4.10)
┌──────────────▼──────────┐  ┌────────────────▼──────────┐  ┌──────────────────────┐
│ Oranda.Core              │  │ Oranda.Language            │  │ Oranda.Platform       │
│ 모델·인터페이스·정책 관문 │  │ 렉서·파서·포매터·자동완성   │  │ 자격 증명·경로·OS 연동 │
│ 실행 파이프라인·이벤트    │  │ (ANTLR 포함, DB 모름)      │  │ (Windows / macOS 구현) │
└──────────────┬──────────┘  └────────────────────────────┘  └──────────────────────┘
┌──────────────▼──────────┐
│ Oranda.Oracle            │  ODP.NET 구현: 접속·점검·실행·메타데이터·모니터링,
│                          │  SQL 카탈로그(*.sql 리소스)
└──────────────────────────┘
```

### 3.2 솔루션 구성
| 프로젝트 | 내용 | 참조 가능 |
|---|---|---|
| `src/Oranda.Core` | 도메인 모델, 서비스 인터페이스, 정책 관문, 실행 파이프라인, 이벤트 버스, SQL 카탈로그 로더 | (없음) |
| `src/Oranda.Language` | SQL·PL/SQL 렉서, 문장 분리, 경량 파서, ANTLR 분석기, 포매터, 자동완성 엔진 | Core |
| `src/Oranda.Oracle` | ODP.NET 기반 구현, SQL 카탈로그 리소스 | Core |
| `src/Oranda.Platform` | `ICredentialStore`(DPAPI/키체인), 앱 데이터 경로, OS 테마 감지 | Core |
| `src/Oranda.App` | Avalonia UI, ViewModel, 그리드 컨트롤, 에디터 통합, 조립(DI 구성) | 전부 |
| `tests/Oranda.Core.Tests` | 정책·파이프라인·모델 단위 테스트 | Core |
| `tests/Oranda.Language.Tests` | 렉서·파서·포매터 회귀(예제 1,000개 이상) | Language |
| `tests/Oranda.Oracle.IntegrationTests` | 실제 DB 시험(컨테이너 26ai, 사내 19c) — `Category=Integration` | Oracle |
| `tests/Oranda.App.Tests` | Avalonia Headless UI 테스트 | App |
| `tests/Oranda.Perf` | 시작 시간·메모리·그리드·파서 측정(BenchmarkDotNet + 시작 측정 도구) | 전부 |

현재 `src/Oranda.Core/Connection/*`(ODP.NET 의존)는 M0에서 `Oranda.Oracle`로 옮기고, Core에는 인터페이스와 순수 모델만 남긴다. **Core·Language는 Oracle 드라이버와 Avalonia를 참조하지 않는다**(빌드 시 검사).

### 3.3 실행 흐름 예: 커서 위치 문장 실행 (EXEC-01)
1. 에디터 ViewModel → `ILanguageService.GetStatementAt(doc, caret)` (렉서 결과로 문장 범위·종류)
2. `IExecutionService.ExecuteAsync(session, statement, binds, ct)`
3. 파이프라인: 치환 변수 → 바인드 수집 → **정책 관문**(읽기 전용·운영 확인·WHERE 없는 DML·라이선스 태그) → 드라이버 실행
4. 결과가 커서면 `IResultSet` 반환 → 그리드가 페이지 단위로 요청
5. 이벤트: `StatementExecuted`(이력 저장), `TransactionStateChanged`(상태바), `DbmsOutputReceived`

---

## 4. 구성 요소 설계

### 4.1 접속 계층 (CONN)
- `ConnectionProfile`(현재 구현됨): 이름, 사용자, 데이터 소스(EZConnect Plus 또는 TNS 별칭), 인증 모드, TNS 디렉터리, 지갑 위치, 환경 라벨, 읽기 전용, 팩 보유 선언, NLS 설정, Keep Alive 간격. 비밀번호는 담지 않는다.
- `OracleConfiguration.TnsAdmin/WalletLocation`은 프로세스 전역이므로 **접속별로 다른 지갑은 연결 문자열의 `Wallet_Location`/`TNS_ADMIN` 속성**으로 지정한다(전역 설정은 기본값으로만).
- **세션 모델**: `ISession` = 물리 연결 1개. 편집 도구는 탭이 공유하는 "작업 세션", 모니터링 도구는 도구마다 "전용 세션"(CONN-07). 풀링은 끈다(`Pooling=false`) — 세션 상태(NLS, 트랜잭션, `DBMS_OUTPUT`)를 사용자가 직접 다루기 때문이다.
- **접속 직후 점검**: `ServerProbe`(구현됨)로 버전·인스턴스·PDB·권한 수집 → `ServerCapabilities`. 추가 항목: ADB 여부(`SYS_CONTEXT('USERENV','CLOUD_SERVICE')`), `control_management_pack_access`, 에디션(`v$version`).
- **지원 버전 관문**: `SupportedVersions` 설정 한 곳에서 판단(19.x, 23.x). 아니면 `UnsupportedServerException`(버전·이유 포함) → 접속 종료·안내(PRD D-06). 23.26 이상은 "26ai"로 표시.
- **서버 추적 정보**: 연결 직후 `ModuleName="Oranda"`, `ActionName=도구명`, `ClientId=OS 사용자`(SAFE-05).
- **취소**: `OracleCommand.Cancel()` + `CancellationToken`. 1 s 안에 응답 없으면 UI는 "취소 중" 표시 후 계속 기다린다(연결을 강제로 끊지 않는다).
- **Keep Alive**: 유휴 시간이 설정값을 넘으면 `SELECT 1 FROM dual`. 실행 중인 세션에는 보내지 않는다.
- **끊김 감지**: ORA-03113/03114/03135/12570 등 → 세션 상태 `Broken`, 미커밋 여부를 알린다(CONN-11).

### 4.2 SQL 카탈로그
- 위치: `src/Oranda.Oracle/Catalog/**/*.sql` (임베디드 리소스). 파일 하나에 쿼리 하나.
- 머리 주석으로 메타데이터를 단다.
```sql
-- id: session.list
-- min-version: 19
-- requires: v$session            -- 권한 판단용(접속 시 점검 결과와 대조)
-- license: none                   -- none | diagnostics | tuning
-- rac: gv                         -- v | gv  (RAC면 gv$ 변형 사용)
SELECT s.inst_id, s.sid, s.serial#, s.username, ...
  FROM gv$session s
 WHERE ...
```
- 선택 규칙: `id`가 같은 후보 중 서버 버전 ≥ `min-version`인 것 가운데 가장 높은 버전. 23ai/26ai 전용 항목(BOOLEAN, VECTOR, Domain, Duality View)은 `min-version: 23`.
- `license`가 `none`이 아니면 정책 관문이 팩 보유 선언을 확인한다(SAFE-07). 선언이 없으면 **쿼리 문자열 자체를 드라이버로 보내지 않는다**.
- 빌드 시 검사: 모든 `.sql`이 머리 주석을 갖는지, `FETCH FIRST` 같은 금지 구문이 19 미만 대상에 없는지(나중 구버전 추가 대비 규칙만 둔다), `id` 중복 여부.
- 나중에 11.2·12c·18c를 추가하면 같은 `id`로 `min-version: 11` 파일만 더한다(PRD 11.3).

### 4.3 실행 엔진 (EXEC)
- **문장 분리**: Language 계층 렉서가 문자열·주석·q-quote·PL/SQL 블록을 인식해 `;`, `/`(단독 줄), 빈 줄 규칙(선택)으로 나눈다. 블록(`DECLARE`/`BEGIN`/`CREATE ... AS|IS`) 안의 `;`는 끝이 아니다.
- **문장 분류**: SELECT/WITH(질의), DML, DDL, PL/SQL 블록, 트랜잭션 제어, SQL*Plus 명령(EXEC-05), 기타. 정책 관문과 결과 처리에 쓴다.
- **바인드**: `:name` 수집 → 대화상자(타입·마지막 값 기억) → `OracleParameter`. 치환 변수 `&`, `&&`는 실행 전 텍스트 치환.
- **트랜잭션 추적**: DML 성공 후 `DBMS_TRANSACTION.LOCAL_TRANSACTION_ID` 확인으로 미커밋 상태 갱신(Orange 방식). 자동 커밋은 기본 끔.
- **DBMS_OUTPUT**: 세션 시작 시 `DBMS_OUTPUT.ENABLE(NULL)`(설정 시), 실행 후 `GET_LINES` 배열로 한 번에 수거.
- **스크립트 실행**: 문장 단위로 순차 실행, 오류 처리(묻기/중지/계속, EXEC-10), 진행 표시, 문장별 결과 탭.
- **이력**: 실행마다 SQLite에 기록(5.3). 바인드 값 저장은 사용자 설정(기본 끔).

### 4.4 결과 그리드 (GRID)
- **데이터 구조**: `IResultSet` → 페이지 캐시(기본 500행/페이지). 열 지향 버퍼에 원시 값 저장(문자열 변환은 그릴 때). 페이지는 LRU로 메모리 상한(설정, 기본 200 MB) 안에서 유지하고, 밀려난 페이지는 다시 가져오지 않고 **임시 파일 캐시**로 내린다(커서는 앞으로만 가므로 재조회 불가).
- **가져오기**: `FetchSize = reader.RowSize × 500`. 화면 끝에 가까워지면 다음 페이지 요청. "모두 가져오기"는 백그라운드 스트리밍.
- **그리기**: 보이는 셀만 `Render`에서 직접 그린다(행·열 가상화, 셀 컨트롤 0개). 텍스트는 셀 단위 `GlyphRun`(ASCII)·`TextLayout`(그 밖) 캐시(D-008, Avalonia 12 `FormattedText` 는 캐시 효과 없음). 편집 시에만 오버레이 편집기 1개.
- **타입 표시**: NUMBER는 `OracleDecimal`로 정밀도 보존, DATE/TIMESTAMP는 세션 NLS 형식, LOB은 앞부분(`InitialLOBFetchSize` 설정값, 기본 4,000자)만 미리 가져오고 나머지는 열 때 스트림으로, BOOLEAN 체크 표시, VECTOR는 "VECTOR(768, FLOAT32)" 요약 후 펼침.
- **복사·내보내기**: 선택 범위 → TSV/CSV/INSERT/IN 목록. 파일 내보내기는 그리드가 아니라 **새 DataReader로 서버에서 다시 스트리밍**(EXP-01).
- **편집(GRID-04, PRD 6.5.1)**:
  1. 편집 모드 진입 시 Language 계층이 SELECT를 분석해 대상 테이블 후보와 결과 열의 출처를 구한다.
  2. 메타데이터 서비스로 대상 테이블의 PK(없으면 NOT NULL 유니크 키) 열을 구한다.
  3. 결과 열 집합에 키 열 전체 또는 ROWID가 없으면 **편집 불가** + 이유 표시. 단일 테이블 SELECT면 "ROWID 추가 후 다시 실행" 버튼으로 `ROWID`를 덧붙인 쿼리를 다시 실행할 수 있다.
  4. 집계·DISTINCT·GROUP BY·집합 연산은 편집 불가. 조인은 대상 테이블이 키 보존이고 키가 결과에 있을 때만, 대상 테이블 열만 편집 가능.
  5. 반영: 변경마다 `UPDATE t SET c=:v WHERE pk=:k`(또는 `ROWID=:rid`). 세이브포인트 → 실행 → 영향 행 수 ≠ 1이면 세이브포인트로 롤백하고 해당 행 표시. 낙관적 잠금 옵션이면 WHERE에 원래 값 비교 추가.
  6. 생성 DML은 반영 전 미리보기(GRID-05)로 보여 주고, 정책 관문을 통과해야 실행된다.

### 4.5 언어 서비스 (EDIT)
**2계층 구조**
| 계층 | 구현 | 용도 | 목표 시간 |
|---|---|---|---|
| L1 실시간 | 자체 렉서 + 경량 하향식 파서 | 강조, 문장 경계, 괄호·블록 짝, 접기, 자동완성 문맥(현재 절, 별칭 표), 바인드 추출 | 키 입력마다, 변경된 줄 주변만 다시 렉싱. 1만 줄 문서에서 16 ms 이하 |
| L2 분석 | ANTLR PL/SQL 문법 | 문법 오류 표시, 포매터 입력, 그리드 편집용 SELECT 분석, 정의로 이동 | 입력이 300 ms 멈춘 뒤 백그라운드. **바뀐 문장만** 다시 파싱 |

- ANTLR은 SLL 예측으로 먼저 시도하고 실패할 때만 LL로 다시 파싱한다. 앱 시작 후 유휴 시간에 작은 예제로 워밍업한다.
- 문법은 grammars-v4 PL/SQL을 저장소에 포함해 빌드 시 생성(Antlr4BuildTasks)하고, 23ai/26ai 구문 보강은 별도 문법 파일 차이로 관리한다.
- 구문 강조: 1단계는 TextMate SQL 문법(AvaloniaEdit.TextMate), M1 안에 L1 렉서 기반 colorizer로 교체해 PL/SQL 키워드·바인드·객체명을 구분한다.
- **자동완성**: L1이 커서 위치의 문맥(FROM 절, `별칭.` 뒤, 패키지명 뒤, 힌트 주석 안)을 판별 → `IMetadataProvider`(4.7)에서 후보 조회. 메타데이터는 메모리 + SQLite 캐시, 150 ms 안에 응답하지 못하면 캐시 결과만 먼저 보여 준다.
- **다중 커서(EDIT-07)**: AvaloniaEdit에 정식 기능이 없다. M0에서 확장 가능성을 시험하고, 어려우면 열 선택(Alt+드래그)으로 대체하고 다중 커서는 P1로 낮춘다(T-D03).

### 4.6 포매터 (FMT)
- **입력**: L1 토큰 열 + 가능하면 L2 구문 트리. 구문 트리를 못 얻는 구간(오류)은 토큰 규칙만 적용하고 원문 배치를 최대한 유지한다.
- **처리**: 토큰 열 → 서식 규칙(대소문자, 줄바꿈 지점, 들여쓰기 깊이, 정렬 열) → 출력.
- **옵션 스키마**: `FormatterOptions`(JSON 직렬화). PRD FMT-02~07 항목을 모두 열거형/정수 속성으로 둔다. 프로필 = 이름 + 옵션 + 버전. 기본 제공 3종(Oranda 기본, Orange 5 유사, 압축형).
- **보장(FMT-11)**: 출력 토큰 열에서 공백·주석 위치를 뺀 값이 입력과 같아야 하고, 주석 수가 같아야 한다. 다르면 결과를 버리고 오류를 알린다. 같은 입력을 두 번 포맷한 결과가 같아야 한다(멱등). 둘 다 회귀 테스트로 검사.
- **제외 구간**: `-- @formatter:off` ~ `on` 구간은 원문 그대로.

### 4.7 메타데이터·스키마 브라우저 (SCH)
- `IMetadataProvider`: 스키마 목록, 객체 목록(유형별), 테이블 열·제약·인덱스·주석, 패키지 멤버·인자, DDL. 뷰 계열(`dba_`/`all_`/`user_`)은 `ServerCapabilities`로 자동 선택.
- 캐시: 접속(DB 고유 이름 + 사용자) 단위. 처음 접속 시 객체 이름 목록만 가져오고, 열·인자는 필요할 때. `all_objects.last_ddl_time`으로 변경 감지 후 부분 갱신.
- DDL: `DBMS_METADATA.GET_DDL` + `SET_TRANSFORM_PARAM`(저장 속성 표시 옵션). 23.7 이상(또는 19.28 이상)이면 `DBMS_DEVELOPER.GET_METADATA` 사용을 M2에서 측정 후 선택.
- 트리는 지연 로딩(펼칠 때 조회), 객체 수가 많은 스키마는 이름 필터를 서버 쿼리에 넣는다.

### 4.8 모니터링 (SESS, SQLM)
- `IMonitorService`가 전용 세션에서 주기 조회(기본 5 s, 2~60 s 설정). 이전 결과와 차이로 델타 계산(SESS-06).
- 쿼리는 카탈로그에서(`session.list`, `session.blocking_tree`, `session.detail.sql`, `sql.top`, `sql.binds` 등), RAC면 `gv$`.
- 블로킹 트리: `blocking_session`/`final_blocking_session`으로 서버에서 계층 구성 후 클라이언트 트리로.
- Kill: `ALTER SYSTEM KILL SESSION 'sid,serial#,@inst' IMMEDIATE` — 정책 관문의 "위험 명령" 확인을 거친다.
- 트레이스: `DBMS_MONITOR.SESSION_TRACE_ENABLE/DISABLE`, 파일 위치는 `v$diag_info`.
- 라이선스 태그 `diagnostics`/`tuning` 쿼리는 선언 없이는 화면 자체가 비활성.

### 4.9 정책 관문 (SAFE)
- `IExecutionPolicy.EvaluateAsync(context, statement)` → `Allow` / `Confirm(메시지)` / `Deny(이유)`.
- 규칙(순서대로): 읽기 전용 프로필이면 질의 외 거부 → 라이선스 태그 확인 → 운영 라벨이면 DML/DDL 확인 → WHERE 없는 UPDATE/DELETE 경고 → 위험 명령(DROP, TRUNCATE, `ALTER SYSTEM`) 이름 재입력 옵션.
- 판단 근거는 Language 계층의 문장 분류. 분류 실패 시 **보수적으로** 가장 엄격한 규칙(DML로 간주)을 적용한다.
- 읽기 전용 프로필은 접속 직후 `SET TRANSACTION READ ONLY`도 함께 건다(이중 방어).
- 모든 실행 경로(편집기, 그리드 편집, 스키마 브라우저 명령, 모니터 Kill, 확장 패널)는 `IExecutionService`만 통해 실행한다. 다른 경로로 `OracleCommand`를 만들지 못하도록 Oranda.Oracle의 실행 클래스는 `internal`로 둔다.

### 4.10 AI 확장 지점 (PRD APP-09)
1.0에서 구현하는 것은 인터페이스와 그것을 실제로 쓰는 기존 기능뿐이다.
| 인터페이스 | 역할 | 1.0에서 쓰는 곳 |
|---|---|---|
| `IMetadataProvider` | 스키마·열·주석·통계 읽기 | 자동완성, 스키마 브라우저 |
| `IEditorContext` | 활성 문서·선택 영역·현재 문장 읽기, 제안 텍스트 삽입(차이 미리보기 후) | 포매터, 템플릿 삽입 |
| `IExecutionService` + `IExecutionPolicy` | 실행은 반드시 정책 관문 통과 | 모든 실행 |
| `IToolPanelRegistry` | 도킹 패널·명령 등록 | 기본 도구 창 등록 |
| `IOutboundConsent` | 외부 전송 전 동의·전송 내용 미리보기 | 1.0에서는 업데이트 확인·충돌 보고 전송 |
나중의 챗봇은 `IToolPanelRegistry`로 패널을 붙이고, 위 인터페이스만 써서 메타데이터를 읽고 SQL을 제안·삽입한다. Core 변경 없이 붙는지를 PRD 9.4 수락 기준의 시험 패널로 확인한다.

### 4.11 UI 구조 (APP)
- Shell: Dock.Avalonia 레이아웃(문서 영역 = 편집기 탭, 도구 창 = 스키마·출력·이력·모니터). 레이아웃은 앱 DTO로 JSON 저장.
- 명령: 모든 기능을 `ICommandRegistry`에 ID로 등록 → 메뉴·툴바·단축키·명령 팔레트가 같은 ID를 쓴다. 키맵 프리셋(기본, Orange 5, PL/SQL Developer, SQL Developer)은 ID→키 표(JSON).
- 테마: Fluent 밝게/어둡게 + 환경 라벨 색(운영 빨강 테두리). 글꼴: 에디터는 사용자 지정, UI는 임베드 Inter + 한글 대체 글꼴 지정(Windows 맑은 고딕, macOS Apple SD Gothic Neo).
- 컴파일 바인딩 기본(`AvaloniaUseCompiledBindingsByDefault=true`).

### 4.12 로컬 저장·자격 증명
| 데이터 | 위치 | 형식 |
|---|---|---|
| 설정, 접속 프로필, 포매터 프로필, 키맵, 레이아웃 | Windows `%APPDATA%\Oranda`, macOS `~/Library/Application Support/Oranda` | JSON (스키마 버전 필드) |
| 실행 이력, 메타데이터 캐시 | 같은 폴더 `oranda.db` | SQLite |
| 작업 공간·자동 저장(복구용) | 같은 폴더 `workspaces/`, `autosave/` | 파일(10 s 간격) |
| 로그 | 같은 폴더 `logs/` (7일 보관) | Serilog 파일 |
| 비밀번호·토큰 | Windows DPAPI 암호화 파일(사용자 범위) / macOS 키체인 | `ICredentialStore` |
- macOS 키체인 접근 권한은 코드 서명 ID에 묶인다. 업데이트 때 서명 인증서가 바뀌면 다시 묻는다 → 서명 인증서를 일관되게 유지한다.
- 관리자가 미리 넣는 설정(M3 이후): 앱 폴더의 `policy.json`이 사용자 설정보다 우선하며 잠금 항목을 가진다.

---

## 5. 데이터 모델 (로컬)

### 5.1 접속 프로필 (`connections.json`)
```json
{
  "schemaVersion": 1,
  "profiles": [{
    "id": "b1f6…", "name": "운영-주문DB", "user": "app_ro",
    "dataSource": "tcps://db01.corp:1522/ORDERS_PDB",
    "auth": "Default", "tnsAdmin": null, "walletLocation": null,
    "environment": "Production", "readOnly": true,
    "licensePacks": [], "keepAliveSeconds": 300,
    "nls": { "dateFormat": "YYYY-MM-DD HH24:MI:SS", "language": null },
    "credentialRef": "oranda:b1f6…"
  }]
}
```
- `environment`: Development | Test | Production. `licensePacks`: [] | ["Diagnostics"] | ["Diagnostics","Tuning"].
- `credentialRef`는 `ICredentialStore` 키. 비밀번호 자체는 이 파일에 없다(자동 검사로 확인, PRD 9.2).

### 5.2 포매터 프로필 (`formatter/*.json`)
```json
{ "schemaVersion": 1, "name": "팀 기본",
  "case": { "keyword": "Upper", "function": "Upper", "identifier": "AsIs" },
  "indent": { "useTabs": false, "size": 4, "subquery": 1, "case": 1, "plsqlBlock": 1 },
  "commaPosition": "Trailing", "operatorSpacing": true, "parenInnerSpace": false,
  "lineBreaks": { "selectList": "OnePerLine", "joinClause": true, "andOr": "Before",
                  "inListThreshold": 5, "maxLineWidth": 120 },
  "align": { "alias": true, "assignment": true, "columnDefinition": true, "equals": false },
  "comments": { "preserve": true, "maxBlankLines": 1 },
  "statementTerminator": "Keep" }
```

### 5.3 SQLite 스키마 (`oranda.db`)
```sql
CREATE TABLE history (
  id INTEGER PRIMARY KEY, profile_id TEXT NOT NULL, executed_at TEXT NOT NULL,
  sql_text TEXT NOT NULL, statement_kind TEXT NOT NULL, elapsed_ms INTEGER,
  rows_affected INTEGER, error_code TEXT, binds_json TEXT /* 사용자 설정 시만 */ );
CREATE INDEX ix_history_profile_time ON history(profile_id, executed_at DESC);
CREATE TABLE meta_object (
  db_key TEXT NOT NULL, owner TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
  last_ddl_time TEXT, payload_json TEXT, PRIMARY KEY (db_key, owner, name, type) );
CREATE TABLE schema_version (version INTEGER NOT NULL);
```
`db_key` = DB 고유 이름 + PDB 이름 + 접속 사용자.

### 5.4 작업 공간 (`workspaces/*.json`)
탭 목록(파일 경로 또는 내용, 커서 위치, 연결 프로필 ID), 활성 탭, 레이아웃 참조.

---

## 6. 내부 API (서비스 인터페이스)
Core에 정의하고 Oracle·Language·Platform이 구현한다. 2개 이상 기능이 쓰므로 **계약으로 먼저 고정**한다(dflow 계약 Task 대상).

```csharp
public interface ISessionManager {
    Task<ISession> OpenAsync(ConnectionProfile profile, SecureCredential credential,
                             SessionPurpose purpose, CancellationToken ct);   // Work | Monitor
    IReadOnlyList<ISession> Sessions { get; }
}
public interface ISession : IAsyncDisposable {
    Guid Id { get; }  ConnectionProfile Profile { get; }  ServerInfo Server { get; }
    SessionState State { get; }            // Idle | Busy | Broken | Closed
    bool HasPendingTransaction { get; }
    Task CommitAsync(CancellationToken ct);  Task RollbackAsync(CancellationToken ct);
}
public interface IExecutionService {
    Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt,
                                       IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct);
    IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts,
                                       ScriptErrorMode mode, CancellationToken ct);
}
public interface IExecutionPolicy {
    ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct);
}
public interface IResultSet : IAsyncDisposable {
    IReadOnlyList<ResultColumn> Columns { get; }
    Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct);
    bool IsComplete { get; }  long? KnownRowCount { get; }
}
public interface IRowEditor {          // GRID-04
    EditCapability Analyze(SqlStatement select, IReadOnlyList<ResultColumn> cols, TableKeyInfo keys);
    IReadOnlyList<SqlStatement> BuildDml(IReadOnlyList<RowChange> changes);   // 미리보기용
    Task<ApplyResult> ApplyAsync(ISession s, IReadOnlyList<RowChange> changes, bool commit, CancellationToken ct);
}
public interface IMetadataProvider {
    Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct);
    Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct);
    Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct);
}
public interface IMonitorService {
    Task<IReadOnlyList<SessionRow>> GetSessionsAsync(ISession monitor, SessionFilter f, CancellationToken ct);
    Task<BlockingTree> GetBlockingTreeAsync(ISession monitor, CancellationToken ct);
    Task<IReadOnlyList<TopSqlRow>> GetTopSqlAsync(ISession monitor, TopSqlCriteria c, CancellationToken ct);
    Task KillAsync(ISession monitor, SessionKey target, bool immediate, CancellationToken ct);
}
public interface ILanguageService {
    IReadOnlyList<SqlStatement> Split(string text, SplitOptions o);
    SqlStatement? GetStatementAt(string text, int offset, SplitOptions o);
    Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct);
    CompletionContext GetCompletionContext(string text, int offset);
}
public interface ISqlFormatter {
    FormatResult Format(string text, FormatterOptions o, TextRange? range = null);  // 토큰 동일성 검사 포함
}
public interface ISqlCatalog { CatalogQuery Get(string id, ServerInfo server); }
public interface ICredentialStore {
    Task SaveAsync(string key, ReadOnlyMemory<byte> secret);
    Task<byte[]?> LoadAsync(string key);  Task DeleteAsync(string key);
}
```
확장 지점 인터페이스(`IEditorContext`, `IToolPanelRegistry`, `IOutboundConsent`, `ICommandRegistry`)는 4.10·4.11 설명대로 Core에 둔다.

---

## 7. 이벤트
Core의 `IEventBus`(약한 참조 구독, UI 스레드 전달은 구독 쪽에서 선택).
| 이벤트 | 발생 | 구독 |
|---|---|---|
| `SessionOpened` / `SessionClosed` / `SessionBroken` | 세션 관리자 | 상태바, 스키마 브라우저, 모니터 |
| `TransactionStateChanged(sessionId, pending)` | 실행 엔진 | 상태바, 종료 확인 |
| `StatementExecuted(sessionId, stmt, result)` | 실행 엔진 | 이력 저장, 출력 창 |
| `DbmsOutputReceived(sessionId, lines)` | 실행 엔진 | 출력 창 |
| `MetadataInvalidated(dbKey, objects)` | DDL 실행 후 / 변경 감지 | 자동완성 캐시, 스키마 트리 |
| `PolicyDecisionMade(stmt, decision)` | 정책 관문 | 로그(감사용 로컬 기록) |
| `SettingsChanged(section)` | 설정 서비스 | 에디터, 그리드, 테마 |

---

## 8. 성능 설계
PRD 7.2 성능 예산을 아래처럼 나눈다.
| 단계 | 예산 | 방법 |
|---|---|---|
| 프로세스 시작 → Avalonia 초기화 | 600 ms | ReadyToRun, 트리밍 검토, 시작 시 어셈블리 로드 최소화(Oracle·ANTLR·TextMate는 시작 경로에서 참조하지 않음) |
| 메인 창 첫 그리기 | 400 ms | 컴파일 바인딩, 첫 화면은 빈 편집기 1개 + 최근 접속 목록만, 도킹 레이아웃 복원은 창 표시 후 |
| 입력 가능 | 500 ms 여유 | DI 구성 지연(필요할 때 생성), 글꼴 열거 피함 |
| 백그라운드(시작 후) | 제한 없음, UI 무영향 | ANTLR 워밍업, TextMate 문법, 최근 접속 메타데이터 캐시 로드 |
- 현재 빈 창 측정은 약 1.8~1.9 s(.NET 10 + Avalonia 12, ReadyToRun, 게시 폴더 235 MB 비압축). .NET 8 + Avalonia 11.3에서는 2.3 s·124 MB였다. M0에서 단계별 시간(프로세스 시작, `AppBuilder` 설정, 창 생성, 첫 렌더)을 `Stopwatch` 계측 로그로 분해해 원인을 찾는다.
- 측정 자동화: `tests/Oranda.Perf`의 시작 측정 도구가 게시본을 5회 실행해 창 핸들 생성·첫 렌더 이벤트까지 시간과 전용 메모리를 기록하고, 예산을 넘으면 실패한다. CI에서 매 빌드 실행(Windows 러너 기준, macOS는 주 1회).
- 메모리: 그리드 페이지 캐시 상한, 메타데이터 캐시는 이름만 상주, LOB은 미리보기만.

---

## 9. 보안 설계
- 자격 증명: 4.12. 메모리에는 필요한 순간만 두고, ODP.NET 23.7 이상의 `OracleOpaqueString` 사용 가능성을 M1에서 확인한다.
- 전송: TCPS는 TLS 1.2 이상, `SSL_SERVER_DN_MATCH=true` 기본. 네이티브 암호화(NNE) 설정은 프로필 고급 항목.
- 로그 마스킹: 연결 문자열의 `Password=` 값, 바인드 값(기본), 지갑 경로 외 비밀값은 로그에 쓰기 전에 지운다(Serilog 필터 + 단위 테스트).
- 외부 전송: 업데이트 확인(사내 업데이트 서버)만 기본 허용. 충돌 보고 전송은 동의 후(`IOutboundConsent`).
- 서명: Windows Authenticode(사내 코드 서명 인증서), macOS Developer ID + 공증. 업데이트 패키지는 Velopack 서명 검증.

---

## 10. 빌드·배포
| 항목 | 내용 |
|---|---|
| 빌드 | `dotnet build Oranda.sln -c Release -warnaserror` |
| 게시(Windows) | `dotnet publish src/Oranda.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` |
| 게시(macOS) | `dotnet publish src/Oranda.App -c Release -r osx-arm64 --self-contained -p:PublishReadyToRun=true` (Windows에서도 생성 가능) |
| 패키징 | `vpk pack` — Windows는 Windows 러너에서 Setup.exe·델타, macOS는 **Mac 러너에서** .app/.pkg + 서명(`--signAppIdentity`) + 공증(`--notaryProfile`) |
| 배포 | 사내 업데이트 서버(정적 파일 서버)에 Velopack 피드 게시. 폐쇄망용 오프라인 설치 파일 별도 |
| CI | Windows x64 러너(빌드·단위·UI·성능·패키징), macOS ARM 러너(빌드·단위·UI·패키징·공증), 통합 테스트는 Docker가 있는 러너에서 `Category=Integration` |
| 버전 | `MAJOR.MINOR.PATCH` + 빌드 번호, `Directory.Build.props`에서 일괄 관리 |
| 공통 빌드 설정 | `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, 컴파일 바인딩 기본. `Directory.Packages.props`로 패키지 버전 중앙 관리 |

macOS 서명 시 필요한 권한(entitlement)은 CoreCLR 기준 `com.apple.security.cs.allow-jit`, `allow-unsigned-executable-memory`, `disable-library-validation`이며 M0에서 실제 공증으로 확정한다.

---

## 11. 테스트 전략
| 종류 | 대상 | 도구 | 실행 시점 |
|---|---|---|---|
| 단위 | Core 정책·파이프라인·모델, Oracle 카탈로그 선택 규칙, 연결 문자열 | xunit | 매 빌드 |
| 언어 회귀 | 렉서·문장 분리·파서·포매터: 예제 1,000개 이상(Oracle 문서 예제, 사내 SQL 익명화본), 토큰 동일성·멱등성 | xunit + 스냅숏 파일 | 매 빌드 |
| UI | 접속 대화상자, 실행·취소, 그리드 편집 불가 표시, 운영 확인 대화상자 | Avalonia.Headless.XUnit | 매 빌드 |
| 통합(컨테이너) | 26ai: 접속·점검·실행·취소·DBMS_OUTPUT·LOB·BOOLEAN·VECTOR·그리드 편집(영향 행 수 검증)·세션 모니터·블로킹 트리 | Testcontainers.Oracle + `gvenzl/oracle-free:23-slim` (amd64·arm64 모두 제공) | 매 PR |
| 통합(19c) | 같은 시나리오를 사내 19c 공유 DB에서 | xunit, 접속 정보는 CI 비밀값 | 매일 |
| 통합(ADB) | 지갑 있음·없음 TLS 접속, ADB 감지·기능 숨김 | 사내 ADB 시험 인스턴스(있으면) | 주 1회 |
| 라이선스 보호 | 팩 "없음" 접속에서 전체 화면을 순회하는 동안 서버 SQL 트레이스에 C-06 객체 호출 0건 | 통합 + 트레이스 분석 스크립트 | 릴리스 전 |
| 성능 | 시작·메모리·키 입력·그리드·포맷 예산(8장) | BenchmarkDotNet + 시작 측정 도구 | 매 빌드(Windows), 주 1회(macOS) |
- 19c 공개 컨테이너 이미지는 없다(Oracle 레지스트리 로그인·라이선스 동의 필요, amd64만). 그래서 19c는 사내 DB로 시험한다.
- Podman도 쓸 수 있다(`DOCKER_HOST`를 Podman 소켓으로, rootless면 `TESTCONTAINERS_RYUK_DISABLED=true`). CI 표준은 Docker.

---

## 12. 비기능 요구 사항 (NFR) 기술 대응
| PRD 항목 | 기술 대응 | 확인 방법 |
|---|---|---|
| 시작 1.5 s 이하 | 8장 단계 예산, ReadyToRun, 지연 로딩 | 성능 테스트(5회 중앙값) |
| 유휴 150 MB 이하 | 시작 경로에서 Oracle·ANTLR 미로드, 메타데이터는 이름만 상주 | 성능 테스트 |
| 키 입력 16 ms 이하(p95, 1만 줄) | L1 증분 렉싱, 강조는 보이는 줄만 | BenchmarkDotNet + UI 계측 |
| 자동완성 150 ms 이하 | 메모리 캐시 우선, 늦으면 캐시 결과 먼저 | 통합 테스트 계측 |
| 문법 검사 300 ms 이하 | 바뀐 문장만 L2 재파싱, 워밍업 | 언어 성능 테스트 |
| 100만 행 스크롤 60 fps | 직접 그리기 가상 그리드, 페이지 캐시 | 그리드 성능 테스트(합성 데이터) |
| 취소 1 s 이하 | `OracleCommand.Cancel` + 토큰, 파이프라이닝 끔 | 통합 테스트(`DBMS_SESSION.SLEEP(600)` 취소) |
| 그리드 편집 사고 0건 | 4.4 편집 규칙, 영향 행 수 검증, 세이브포인트 | 통합 테스트(동시 삭제·중복 키 시나리오) |
| 비밀번호 평문 0건 | `ICredentialStore`, 로그 마스킹 | 설정 폴더·로그 전체 검색 테스트 |
| 설치 파일 80 MB 이하 | Velopack 압축, 트리밍 검토 | 패키징 단계 크기 검사 |

---

## 13. 제약 사항
| ID | 제약 | 영향 |
|---|---|---|
| TC-01 | 그리드 편집은 PK(또는 NOT NULL 유니크 키) 전체 또는 ROWID가 결과에 있을 때만(PRD C-01) | `IRowEditor.Analyze`가 거부하면 UI는 읽기 전용 |
| TC-02 | 드라이버는 ODP.NET Core Managed만(PRD C-02). 파이프라이닝은 끈다 | 비동기 취소·타임아웃 유지 |
| TC-03 | 지원 서버 19c·23ai/26ai·ADB(PRD C-03, D-06). 카탈로그는 버전 키 구조 유지 | 구버전 추가 시 SQL 파일만 더함 |
| TC-04 | Core·Language는 Avalonia·ODP.NET을 참조하지 않는다 | 빌드 검사(아키텍처 테스트) |
| TC-05 | 모든 SQL 실행은 `IExecutionService` 경유 | 실행 클래스 `internal`, 아키텍처 테스트 |
| TC-06 | 라이선스 태그 쿼리는 선언 없이 드라이버로 보내지 않는다(PRD C-06) | 카탈로그 메타데이터 + 정책 관문 |
| TC-07 | macOS 패키지는 Mac 러너에서만 생성·서명·공증 | CI에 macOS ARM 러너 필요 |
| TC-08 | 유료 UI 구성 요소를 쓰지 않는다(TreeDataGrid 등) | 결과 그리드 자체 구현 |
| TC-09 | 테스트 프레임워크는 xunit v3로 통일한다(Avalonia 12 Headless 지원) | M0에서 기존 xunit v2 프로젝트 전환 |

---

## 14. 기술 결정 사항
| ID | 질문 | 제안 | 상태 |
|---|---|---|---|
| T-D01 | 런타임: .NET 8 지원 종료(2026-11-10) 대응 | **A안 — .NET 10 + Avalonia 12.** 2026-10-19 전환 완료(빌드 경고 0, 테스트 통과) | 결정 |
| T-D02 | 결과 그리드 | 직접 그리기 가상 그리드 확정, TreeDataGrid 포크 제외(D-008). 60 fps 는 로컬 콘솔 재측정 필요 | 결정 |
| T-D03 | 다중 커서 | 포크 없는 AvaloniaEdit 확장(앵커+배경 렌더러+터널 입력). 1.0 범위는 D-007 | 결정 |
| T-D04 | 파서 | L1 자체 렉서·경량 파서 + L2 ANTLR(grammars-v4 PL/SQL) | 제안 |
| T-D05 | macOS 자격 증명 | 키체인 P/Invoke 직접 구현(150줄 안팎) | 제안 |
| T-D06 | 업데이트 | Velopack + 사내 정적 파일 서버 | 제안 |
| T-D07 | NativeAOT | 1.0은 ReadyToRun. M0에서 AOT 경고 수 측정 후 재판단 | 제안 |

---

## 15. 기술 위험
| 위험 | 영향 | 대응 |
|---|---|---|
| ANTLR PL/SQL 문법의 첫 파싱·대형 문장 지연 | 문법 검사 300 ms 초과 | SLL 우선, 워밍업, 문장 단위 재파싱. 그래도 넘으면 오류 표시만 지연 표시로 완화 |
| 자체 그리드 구현 범위(선택·복사·열 크기·접근성) | 일정 증가 | M0에서 최소 기능 시험, 접근성은 M3에 별도 작업 |
| Dock.Avalonia API 변경이 잦음 | 업그레이드 시 수정 비용 | 레이아웃 DTO로 감싸 교체 가능하게 둔다 |
| 빈 창 시작 1.8~1.9 s(.NET 10 + Avalonia 12, ReadyToRun 실측. .NET 8 + 11.3은 2.3 s) | 시작 예산 1.5 s 미달 | 8장 계측으로 원인 분해. 미달 시 PRD 목표 재협의 근거 제시 |
| ODP.NET 트리밍·AOT 호환 미확인 | 크기·시작 최적화 제한 | 트리밍 시 ODP.NET 어셈블리 전체 보존 |
| macOS 키체인이 서명 변경에 묶임 | 업데이트 후 비밀번호 재입력 요청 | 서명 인증서 고정, 갱신 절차 문서화 |

---

## 16. 수락 기준 (기술)
- [ ] 아키텍처 테스트: Core·Language 프로젝트가 Avalonia·Oracle 어셈블리를 참조하면 빌드가 실패한다.
- [ ] 아키텍처 테스트: `Oranda.Oracle` 밖에서 `OracleCommand`를 만드는 코드가 없다.
- [ ] SQL 카탈로그 검사: 모든 `.sql`에 `id`·`min-version`·`license` 머리 주석이 있고 `id` 중복이 없다.
- [ ] 라이선스 태그가 `diagnostics`/`tuning`인 쿼리는 팩 선언 없는 접속에서 드라이버 호출 0건이다(단위 테스트로 가짜 드라이버 호출 수 확인).
- [ ] 26ai 컨테이너 통합 테스트가 Windows와 macOS(ARM) 러너에서 통과한다.
- [ ] 포매터 회귀: 예제 전체에서 토큰 동일성·멱등성 위반 0건.
- [ ] 그리드 편집 통합 테스트: 키 없는 결과 편집 거부, 영향 행 수 0·2건 시나리오에서 롤백 확인.
- [ ] 성능 테스트가 8장 예산을 CI에서 측정해 기록하고, 예산 초과 시 빌드를 실패시킨다(M1부터 시작·메모리, M2부터 전 항목).
- [ ] 비밀번호·바인드 값이 설정 파일·로그·SQLite에 평문으로 없다(검색 테스트).
- [ ] Windows 설치 파일과 macOS .pkg가 서명·공증되고, 업데이트 시 서명 검증이 실패하면 설치하지 않는다.

---

## 17. Dev Config (dflow용)
```markdown
## Dev Config

### Domains
| domain | description | unit-test | e2e-test | e2e-server | e2e-url |
|--------|-------------|-----------|----------|------------|---------|
| backend | Core·Language·Oracle·Platform (UI 없는 계층) | `dotnet test tests/Oranda.Core.Tests && dotnet test tests/Oranda.Language.Tests` | `dotnet test tests/Oranda.Oracle.IntegrationTests --filter Category=Integration` | - | - |
| frontend | Avalonia UI (Oranda.App) | `dotnet test tests/Oranda.App.Tests` | - | - | - |
| database | Oracle SQL 카탈로그 | `dotnet test tests/Oranda.Core.Tests --filter Category=Catalog` | - | - | - |
| fullstack | 화면 + 서비스 + 카탈로그 수직 슬라이스 | - | - | - | - |

### Design Guidance
| domain | architecture |
|--------|-------------|
| backend | 서비스 인터페이스는 Oranda.Core에, 구현은 Oranda.Oracle/Language/Platform에 둔다. 모든 SQL 실행은 IExecutionService와 정책 관문을 거친다. 딕셔너리 SQL은 Catalog/*.sql 파일(머리 주석 필수)로만 추가한다. |
| frontend | MVVM(CommunityToolkit.Mvvm), 컴파일 바인딩. 새 도구 창은 같은 Task에서 IToolPanelRegistry 등록과 ICommandRegistry 명령·메뉴·단축키 연결까지 끝낸다(진입점 없는 화면 금지). UI 스레드에서 DB 호출 금지. |

### Quality Commands
| name | command |
|------|---------|
| lint | `dotnet format Oranda.sln --verify-no-changes` |
| typecheck | `dotnet build Oranda.sln -warnaserror` |
| coverage | `dotnet test Oranda.sln --collect:"XPlat Code Coverage"` |

### Cleanup Processes
dotnet, testhost, Oranda.App
```

---

## 18. 용어 정의
| 용어 | 뜻 |
|---|---|
| 작업 세션 / 전용 세션 | 편집 도구가 공유하는 연결 / 모니터링 도구가 혼자 쓰는 연결 |
| SQL 카탈로그 | 버전·권한·라이선스 태그가 붙은 딕셔너리 SQL 파일 모음 |
| 정책 관문 | 모든 SQL 실행 전에 읽기 전용·운영 확인·라이선스 규칙을 적용하는 단계 |
| L1 / L2 | 키 입력마다 도는 경량 렉서·파서 / 백그라운드에서 도는 ANTLR 전체 파서 |
| 페이지 캐시 | 결과 행을 500행 단위로 나눠 메모리·임시 파일에 두는 구조 |
| ReadyToRun | 배포 시 미리 기계어로 컴파일해 시작 시간의 JIT 비용을 줄이는 .NET 기능 |
| Velopack | Windows·macOS 설치 파일과 자동 업데이트를 만드는 오픈소스 도구 |
| 키 보존(key-preserved) | 조인 결과의 각 행이 특정 테이블의 행 하나에 대응하는 성질 |

---

## Assumptions (auto-resolved 2026-10-19)
- `prd-validate`가 지적한 vague_metric 10건은 C# 비동기 메서드 이름(`ExecuteAsync`, `SaveAsync` 등)의 `eAsy` 부분이 "easy"로 잘못 잡힌 것이다. 요구사항의 모호한 표현이 아니다. .NET 이름 규칙(`…Async`)을 유지하고 문서는 고치지 않는다.
