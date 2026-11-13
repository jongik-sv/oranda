# 되돌리기 지점 구현 회차 (rollback-1113)

- 설계 정본: `docs/superpowers/specs/2026-11-13-rollback-point-design.md`
- 승인된 시안(3판): `docs/superpowers/specs/2026-11-13-rollback-point-proposal.html`
- 통합 브랜치: `dev-jji-mac`. 조정자: win144. 레인 PC: win144 · Mac · win30
- 설계자 결정 RD-1~RD-21 은 설계 문서 20장에 있습니다. 사용자는 남은 결정을 설계자에게 맡겼습니다(2026-11-13).

## 항목

| id | 제목 | 크기 | 건드릴 파일 | 선행 | 시험 범위 | 레인 |
|---|---|---|---|---|---|---|
| RB-00 | 공유 계약(단독 선머지) | M | `src/Oranda.Core/Rollback/*.cs`(최상위만), `Core/Execution/{SqlStatement,ExecutionResult,IExecutionService,IExecutionInteraction}.cs`, `Core/Execution/Providers/PolicyGate.cs`, `Core/Settings/AppSettings.cs`, `Core/Storage/IAppDataPaths.cs`, `App/Features/Rollback/RollbackCommandIds.cs`, `App/Shell/Composition/RollbackComposition.cs`(+`ServiceContainer.Features.cs` 1줄), `tests/Oranda.Testing/Rollback/**`, `tests/Oranda.Core.Tests/Rollback/Contract*.cs`, `docs/design/data-model.md` | - | 솔루션 빌드, Core.Tests, Architecture.Tests | M1 |
| RB-01 | 값 읽기 식과 리터럴(Oracle 계열 공용) | M | `Core/Rollback/Literals/**` + 시험 | RB-00 | Core.Tests | M1 |
| RB-02 | 캡처 엔진 + `RollbackFlow` + 데이터 가져오기 SCN 전용 | L | `Core/Rollback/Capture/**`, `Core/Transfer/DataTransferService.cs`(1줄), `RollbackComposition.cs` + 시험 | RB-00, RB-01 | Core.Tests | M1 |
| RB-03 | 스크립트 본문 작성기(역순) + 역문장 빌더 | M | `Core/Rollback/Script/**` + 시험 | RB-00, RB-01 | Core.Tests | M1 |
| RB-04 | 원장(커밋 묶음, 저장점, 트랜잭션 끝 확인, 열린 묶음 저장, 기동 복구) | M | `Core/Rollback/Ledger/**`, `RollbackComposition.cs` + 시험 | RB-00 | Core.Tests | M1 |
| RB-10 | Oracle 제공자 연결(채널, 방언, SCN, 저장점, RETURNING, 트랜잭션 끝, `OracleSession`) | L | `src/Oranda.Oracle/Execution/OracleExecutionService.cs`, `Oracle/Sessions/OracleSession.cs`, `Oracle/Rollback/**`, `Oracle/Catalog/rollback/**`, `tests/Oranda.Oracle.Tests/Rollback/**` | RB-00(가짜로 먼저 시작), RB-02, RB-04 | Oracle.Tests(단위만) | M2 (두 번 머지: 10a = 방언·통로·카탈로그, 10b = 실행 서비스·세션) |
| RB-11 | Tibero 제공자 연결(TSN, `AS OF TSN`, 오류 코드 분류, 스크립트 Tibero 경로) | L | `src/Oranda.Tibero/Execution/TiberoExecutionService.cs`, `TiberoErrorClassifier.cs`, `Sessions/TiberoSession.cs`, `Tibero/Rollback/**`, `Tibero/Catalog/rollback/**`, `tests/Oranda.Tibero.Tests/Rollback/**` | RB-00, RB-02, RB-04(RB-10 형식을 따름) | Tibero.Tests(가짜 드라이버) | T1 |
| RB-12 | Oracle 통합 시험(문장별 끝까지, 리터럴 왕복, 실패 뒤 잠금 해제, ROLLBACK TO, RETURNING, PL/SQL·DDL 확인, VERSIONS_XID, CTAS) | M | `tests/Oranda.Oracle.IntegrationTests/Rollback/**` | RB-10, RB-02, RB-03, RB-04, RB-30 | **CI 만** | M2 |
| RB-13 | Tibero 실서버 확인 후 방언 값 확정 | M | `tests/Oranda.Tibero.Tests/Integration/Rollback*It.cs`, `docs/design/tibero-driver-exec.md`, `docs/design/tibero-catalog-map.md`, `Tibero/Rollback/**` | RB-11 | win30 의 Tibero 실서버 | T1 |
| RB-14 | Tibero 백업 테이블 권유와 TRUNCATE 변경 일지 | S | `TiberoExecutionService.cs`, `tests/Oranda.Tibero.Tests/**` | RB-44, RB-11 | Tibero.Tests | T1 |
| RB-20 | 금고(AES-256-GCM, 키는 자격 증명 저장소, Windows DPAPI 실측) | S | `src/Oranda.Platform/Rollback/RollbackVault.cs` + 시험 | RB-00 | Platform.Tests(Windows) | W1 |
| RB-21 | 저장소 + 마이그레이션 0005 | M | `Platform/Rollback/SqliteRollbackStore.cs`, `Platform/Migrations/OrandaDb/0005_rollback.sql`, `Platform/Storage/**`(rollback 폴더만) + 시험 | RB-00, RB-20 | Platform.Tests | W1 |
| RB-22 | 기동 정리(열린 묶음 → 「확인 필요」, 고아 파일, 보관 기간) | S | `Platform/Rollback/RollbackMaintenance.cs` + 시험 | RB-21 | Platform.Tests | W1 |
| RB-23 | 매뉴얼 그림 Windows 렌더 | S | `site/src/assets/screens/rollback-*.png` | RB-46 | `ManualRollbackScreens`(헤드리스) | W1 |
| RB-30 | DML 형태 분석기(`IDmlAnalyzer`) + `RowDmlBuilder` | M | `src/Oranda.Language/Editing/{DmlShapeAnalyzer.cs,RowDmlBuilder.cs}`, `tests/Oranda.Language.Tests/Editing/**` | RB-00 | Language.Tests | W2 |
| RB-31 | 시점 비교 계획기 + 그리드 G1 어댑터 | M | `Core/Rollback/Compare/**`, `App/Features/Flashback/FlashbackTarget.cs`, `tests/Oranda.Core.Tests/Rollback/Compare/**` | RB-00, RB-01 | Core.Tests, App.Tests(`Flashback`) | W2 |
| RB-32 | 화면 3(실행 전과 비교) | L | `App/Features/PointCompare/**`, `tests/Oranda.App.Tests/Features/PointCompare/**` | RB-31(실행 때 RB-21, RB-04) | App.Tests(헤드리스) | W2 |
| RB-33 | 그리드 G2(「이전 값으로 되돌리기」가 화면 3 을 엶) | M | `App/Features/Flashback/FlashbackCommands.cs`, `tests/Oranda.App.Tests/Features/Flashback/**`, `site/src/content/docs/run/grid-edit.md` | RB-32 | App.Tests | W2 |
| RB-40 | 설정 「되돌리기」 탭 | M | `App/Shell/Settings/{RollbackSettingsPage.cs,SettingsWindow.axaml,SettingsViewModel.cs}`, `tests/Oranda.App.Tests/Shell/Settings/Rollback*` | RB-00 | App.Tests | M3 |
| RB-41 | 확인 창 줄(예상 행 수, 경고, 백업 확인란), 실행 로그 줄, 커밋 전 경고, `LobWriter` SCN 전용 | M | `App/Features/Execution/**`, `App/Features/LobRowView/LobWriter.cs`, `tests/Oranda.App.Tests/Features/Execution/Rollback*` | RB-00(끝까지는 RB-10 뒤) | App.Tests | M3 |
| RB-42 | 변경 일지 화면 1(커밋 묶음) | L | `App/Features/ChangeJournal/**`, `tests/Oranda.App.Tests/Features/ChangeJournal/**` | RB-00(처음엔 메모리 저장소), RB-21 | App.Tests | M3 |
| RB-43 | 원복 스크립트 탭과 원복 실행, 화면 2 | L | `App/Features/Rollback/**`(`RollbackCommandIds.cs` 제외), `tests/Oranda.App.Tests/Features/Rollback/**` | RB-03, RB-04, RB-21 | App.Tests | M3 |
| RB-44 | 백업 테이블(CTAS) 권유 + TRUNCATE 변경 일지(Oracle) | M | `Core/Rollback/Backup/**`, `Core/Journal/ChangeTarget.cs`, `Language/Lexing/StatementClassifier.cs`, `OracleExecutionService.cs` + 시험 | RB-10 | Core.Tests, Language.Tests, Oracle.Tests | M2 |
| RB-45 | 매뉴얼 본문 | M | `site/src/content/docs/run/rollback.md`(새 파일), `run/execute.md`, `tools/db-tools.md`, `settings/options.md`, `site/astro.config.mjs` | RB-40~43, RB-44, RB-32 | 사이트 빌드 | M3 |
| RB-46 | 매뉴얼 그림 시험(헤드리스) | S | `tests/Oranda.App.Tests/Manual/ManualRollbackScreens.cs` | RB-40~43, RB-32 | App.Tests(`Manual`) | M3 |
| RB-90 | 성능 시험(10만 행 UPDATE/DELETE, 운영 접속 그리드 한 행, SCN 전용 부하) | M | `tests/Oranda.Oracle.IntegrationTests/Rollback/RollbackPerfIt.cs` | RB-10, RB-12, RB-21 | **CI 만** | M2 |

## 레인

| 레인 | PC | 항목 | owned | forbidden |
|---|---|---|---|---|
| M1 mac-core | Mac | RB-00~04 | `src/Oranda.Core/Rollback/**`(Compare/·Backup/ 제외), RB-00 에 적은 Core 파일, `Core/Transfer/DataTransferService.cs`, `App/Shell/Composition/RollbackComposition.cs`, `tests/Oranda.Testing/Rollback/**`, `tests/Oranda.Core.Tests/Rollback/**`(Compare/·Backup/ 제외), `docs/design/data-model.md` | `Core/Rollback/{Compare,Backup}/**`, `src/Oranda.{Oracle,Tibero,Platform,Language}/**`, 나머지 `src/Oranda.App/**`, `site/**` |
| M2 mac-oracle | Mac | RB-10, 12, 44, 90 | `src/Oranda.Oracle/**`, `Core/Rollback/Backup/**`, `Core/Journal/ChangeTarget.cs`, `Language/Lexing/StatementClassifier.cs`, `tests/Oranda.Oracle.Tests/**`, `tests/Oranda.Oracle.IntegrationTests/**`, `tests/Oranda.Core.Tests/Rollback/Backup/**`, `tests/Oranda.Language.Tests/Lexing/**` | `src/Oranda.Tibero/**`, 다른 Core 파일, `src/Oranda.{App,Platform}/**`, `Language/Editing/**` |
| M3 mac-app | Mac | RB-40~43, 45, 46 | `App/Features/{Rollback,ChangeJournal,Execution}/**`(`RollbackCommandIds.cs` 제외), `App/Features/LobRowView/LobWriter.cs`, `App/Shell/Settings/**`, 대응 시험 `tests/Oranda.App.Tests/Shell/Settings/**`·`tests/Oranda.App.Tests/Features/{Execution,ChangeJournal,Rollback}/**`, RB-45 의 사이트 문서, `site/astro.config.mjs` | `src/Oranda.{Core,Oracle,Tibero,Platform,Language}/**`, `App/Features/{PointCompare,Flashback}/**`, `site/.../run/grid-edit.md`, `site/src/assets/screens/**` |
| W1 win144-store | win144 | RB-20~23 | `src/Oranda.Platform/Rollback/**`, `Platform/Migrations/OrandaDb/0005_rollback.sql`, `Platform/Storage/**`(rollback 폴더만), `tests/Oranda.Platform.Tests/Rollback/**`, `site/src/assets/screens/rollback-*.png` | 나머지 `src/**`, `docs/design/data-model.md` |
| W2 win144-compare | win144 | RB-30~33 | `Language/Editing/{DmlShapeAnalyzer,RowDmlBuilder}.cs`, `Core/Rollback/Compare/**`, `App/Features/{PointCompare,Flashback}/**`, `tests/Oranda.Language.Tests/Editing/**`, `tests/Oranda.Core.Tests/Rollback/Compare/**`, `tests/Oranda.App.Tests/Features/{PointCompare,Flashback}/**`, `site/.../run/grid-edit.md` | `src/Oranda.{Oracle,Tibero,Platform}/**`, 다른 Core·App 파일, `Language/Lexing/**` |
| T1 win30-tibero | win30 | RB-11, 13, 14 | `src/Oranda.Tibero/**`, `tests/Oranda.Tibero.Tests/**`, `tests/Oranda.Tibero.FakeDriver/**`, `docs/design/tibero-*.md` | `src/Oranda.{Oracle,Core,App,Platform,Language}/**` |

공유·동결 파일:
- `strings-ko.json`·`strings-en.json`·`literals-en.json` 은 덧붙이기만 합니다. 키 접두어는 레인별(`Rollback.*`·`PointCompare.*`·`Settings.rollback.*`)이고, 충돌은 조정자가 양쪽을 다 남겨 풉니다.
- `RollbackCommandIds.cs` 와 `ServiceContainer.Features.cs` 의 한 줄은 RB-00 뒤 동결합니다.
- `docs/wbs.md`·`docs/decisions.md`·설계 문서는 조정자 몫입니다. 레인은 어긋남을 보고만 합니다.

## 의존 그래프

```
RB-00 → RB-01, RB-04, RB-10, RB-11, RB-20, RB-30, RB-40, RB-41, RB-42
RB-01 → RB-02, RB-03, RB-31
RB-02 → RB-10, RB-11, RB-12
RB-03 → RB-12, RB-43
RB-04 → RB-10, RB-11, RB-12, RB-43
RB-30 → RB-12
RB-10 → RB-12, RB-44, RB-90
RB-11 → RB-13, RB-14
RB-44 → RB-14, RB-45
RB-20 → RB-21
RB-21 → RB-22, RB-42, RB-43, RB-90
RB-31 → RB-32
RB-32 → RB-33, RB-45, RB-46
RB-12 → RB-90
RB-40, RB-41, RB-42, RB-43 → RB-45, RB-46
RB-46 → RB-23
```

- 진짜 선행물이 들어오기 전에 `tests/Oranda.Testing/Rollback` 의 가짜로 먼저 할 수 있는 항목: RB-10·RB-11(RB-02/RB-04 전), RB-42(RB-21 전).
- 임계 경로: RB-00 → RB-01 → RB-02 → RB-10 → RB-12 → RB-90. M1 이 병목이므로 다른 레인은 가짜로 시작합니다.

## 확신이 낮은 지점(레인이 실측으로 확인)

- `LOCAL_TRANSACTION_ID`(`usn.slot.sqn`) → RAW XID 변환의 바이트 순서. RB-12 에서 측정하고, 실패하면 화면 3 은 WHERE 범위로 물러납니다.
- Tibero 실측(RB-13) 결과: RETURNING out-bind 됨, SAVEPOINT/ROLLBACK TO 됨(잠금도 풂), 형식 마스크(FF9·TZR·TM9·SYYYY)·UNISTR 리터럴 왕복 됨, BINARY_FLOAT 는 `TO_BINARY_FLOAT`(설계 RD-22), VERSIONS 안 됨(TBR-8022, 시점 비교는 WHERE 범위부터), TBR-21003·21034 = 시점 잃음, 정의 바뀜·잘못된 시점 번호에 해당하는 오류 없음.
- 내부 저장점 때문에 실패한 문장 뒤 세션이 「커밋 안 됨」으로 보일 수 있습니다.
- 실패한 DDL, 트랜잭션이 남은 채 ODP.NET 을 닫는 경우의 암묵 커밋은 「모름 → 확인」으로 처리하지만 실제 동작은 미확인입니다.
- 운영 접속 그리드 편집은 행마다 왕복이 3~4번 늘어납니다. 허용 여부와 상한 100000행 기본값은 RB-90 이 판단합니다.
- 파티션 키 UPDATE(ROW MOVEMENT)는 ROWID 가 바뀌어 실행 직후 값 읽기가 빠질 수 있습니다(「불완전」).
- 64자를 넘는 NUMBER 는 `TO_CHAR(..,'TM9')` 왕복이 안 될 수 있습니다.

- App 시험 경로는 기능 화면 = `tests/Oranda.App.Tests/Features/<기능>/`, 설정 = `tests/Oranda.App.Tests/Shell/Settings/` 입니다.

## 시험 규칙

- Oracle 통합 시험(Testcontainers)은 로컬에서 돌리지 않고 CI 에서만 돌립니다. 레인은 바뀐 시험 프로젝트만 `dotnet test -c Debug` 합니다.
- 성능 수치 측정은 RB-90 에서만 합니다.
- 실제 회사 DB 별칭·호스트·IP 는 시험·문서·커밋에 넣지 않습니다(prod-example·dev-example).
