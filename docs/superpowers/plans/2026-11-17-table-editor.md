# 테이블 편집기 · 반영 묶음 구현 계획

> **For agentic workers:** 이 계획은 `/coordinator start table-editor` 회차의 분해 입력이다. 「레인 분해표」가 레인·소유 범위·선행 관계를 정한다. Steps use checkbox (`- [ ]`) syntax for tracking.
> superpowers:writing-plans 스킬은 win144 에 설치되어 있지 않아, 같은 형식으로 쓴 이전 계획(`2026-11-06-cross-connection-data-copy.md`)의 꼴을 따랐다(TE-25).

**Goal:** 객체 상세에서 ✎ 편집을 켜고 열·제약·인덱스를 고치면 바뀐 만큼의 DDL 이 아래에 보이고 실행·스크립트 저장·반영 묶음 넣기를 하는 테이블 편집기와, 개발 중 실행한 DDL 을 모아 환경별 실행 표시·의뢰용 `.sql` 저장을 하는 반영 묶음을 Oracle·Tibero 같은 수준으로 만든다(1차). 이어서 같은 회차에서 파티션·저장 속성·LOB 저장 절·테이블 이름 바꾸기·신원/숨김/도메인 열·함수 기반/비트맵 인덱스·새 테이블 작업 저장·자동 데이터 변환·특수 테이블 생성을 더한다(2차, 설계 18장).

**Architecture:** 편집 모델·차이·문장·순서·거짓 차이 정규화·이름 규칙·의뢰 스크립트 형식은 순수 로직으로 `Oranda.Core/Schema/TableEdit` 에 둔다. DBMS 차이는 `ITableEditDialect`(Oracle·Tibero 가 `[OrandaService(..., Dbms = …)]` 로 구현, `IColumnReorderScripts` 와 같은 꼴)와 실행 전 검사 읽기 `ITableEditChecks` 로 넘긴다. 묶음 저장은 `Oranda.Platform/ChangeBundle`(마이그레이션 `0006_change_bundle.sql`), 자동 넣기는 `StatementExecuted` 구독(`IStartupTask`), 소유자 붙이기·바꾸기는 렉서가 있는 `Oranda.Language` 에 둔다(Core 는 Language 를 참조하지 않는다). 화면은 `Oranda.App/Features/TableEdit`(객체 상세 편집 모드·새 테이블 탭·검사 창)과 `Oranda.App/Features/ChangeBundle`(아래 도구 창, Order 46).

**Tech Stack:** .NET 10, C#, Avalonia 12, ODP.NET Core, Tibero.DataAccess(로컬 피드), SQLite(Microsoft.Data.Sqlite), xUnit, Avalonia Headless, Testcontainers(`gvenzl/oracle-free`).

**Spec:** `docs/superpowers/specs/2026-11-17-table-editor-design.md`(이하 「설계」). 절 번호(예: 설계 8.3)는 설계 문서의 절이다. 2차 설계 = 설계 18장(이 계획과 같은 커밋에서 더함).

## Global Constraints

- 사용자에게 묻지 않는다(2026-11-17 사용자 지시). 막히는 결정은 가장 나은 쪽을 골라 진행하고 설계 16장(TE-25~)·회차 `WBS.md` 결정 목록·answer 파일에 남긴다.
- 사용자가 본 DDL 미리보기 = 실행되는 문장 = 저장되는 문장(설계 4장). 미리보기 문자열과 실행 `SqlStatement.Text` 는 같은 객체에서 나온다.
- 편집을 켜고 아무것도 안 고치면 문장 0개. 고쳤다 되돌려도 0개(설계 8.4). Oracle·Tibero 실측 테이블로 시험.
- 사용자 쓰기는 모두 `IExecutionService` 정책 관문을 지난다. 운영은 SAFE-03 확인 한 번 + `ExecutionOptions.PreConfirmed` 로 문장별 실행(TE-21). `AsSingleBlock` 을 쓰지 않는다.
- 검사·가상 식 미리보기 읽기는 실행 엔진 밖(메타데이터 공급자처럼)으로, 이력·일지·묶음에 남기지 않는다. 시간 제한 기본 10초, 넘으면 ⚠「확인 못 함」.
- Core 는 다른 Oranda 어셈블리를 참조하지 않는다(`tests/Oranda.Architecture.Tests`). 의미 판단은 `DbErrorKind`(D-046).
- D-006: App 은 DBMS 서비스를 `DbmsServices.For<T>(services, session)` 로 꺼낸다. 새 서비스 구현은 `[OrandaService(typeof(인터페이스), Dbms = …)] internal sealed`.
- 식별자는 `SqlIdentifier.Quote`(대문자 규칙을 벗어날 때만 따옴표, 설계 8.2), 소유자는 늘 붙인다. 리터럴은 `SqlLiteral.Quote`.
- 시험 데이터·접속 이름은 예시 값만(`dev-example`, `SAMPLE.ORDERS`, `SAMPLE.ORDER_ITEMS`). 회사 DB 별칭·호스트·IP 금지. Tibero 비밀 값은 win30 `~/.coord/win30-handoff/tibero-test-db.secret`·win144 사용자 환경 변수에만.
- Tibero 시험 계정은 WAS 와 공유 → `ORT_TE_` 접두 객체만 만들고 지운다. 두 번째 계정은 DROP 거부 트리거가 있어 쓰지 않는다.
- 시험은 `-c Debug`. Release 출력 다시 빌드 금지(사용자 앱이 돈다). 전체 시험은 조정자 최종 통합에서 1번.
- 실앱 키보드·마우스 조작 금지. UI 확인 = Avalonia Headless. 실앱 확인 항목은 answer-2 의 「사용자 확인」으로(TE-27).
- 커밋 = 영어 Conventional Commits, 끝 줄 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. 세 브랜치는 ff 로만, force push 금지.

## 정한 것 (계획 단계 결정, 설계 16장에 같이 적음)

| 번호 | 결정 | 이유 |
|---|---|---|
| TE-25 | 계획서는 기존 계획 형식으로 씀(writing-plans 스킬 없음) | win144 에 스킬이 없음 |
| TE-26 | Mac 은 16:30 까지 T2(이름 규칙·의뢰 스크립트 형식) 하나만. 못 끝내면 win144 레인이 이어받음 | Mac 가용 시간 50분 |
| TE-27 | 「Windows 실제 앱 확인」은 자동 조작하지 않고 Headless 시험 + 사용자 확인 목록으로 | 요청의 GUI 조작 확인 규칙과 「묻지 않음」이 겹쳐, 묻지 않고 조작하지 않는 쪽을 고름 |
| TE-28 | TE-4 「캐시 무시하고 새로 읽기」 = `IMetadataCache.InvalidateAsync(dbKey,[table])` 뒤 `GetTableAsync`. 새 플래그를 만들지 않음 | 열 순서 바꾸기와 같은 경로, 공급자 계약 그대로 |
| TE-29 | 메타데이터에 모자란 값은 init 멤버로 더함: `ColumnInfo.CharSemantics`(BYTE/CHAR/null), `ConstraintInfo.DeleteRule`·`ReferencedColumns`, `IndexInfo.Descending`(열별)·`Tablespace`·`ConstraintName`(PK·UK 가 만든 인덱스), `TableInfo.Tablespace`·`Kind`(Heap/Temporary/External/Iot/Cluster/Partitioned). 캐시 `PayloadVersion` 3 | 지금 `TableInfo` 로는 FK 삭제 규칙·DESC·제약 인덱스·특수 테이블 판정을 못 함 |
| TE-30 | 가상 식은 지금처럼 `DefaultValue` 에 담겨 오므로 편집 모델에서 `VirtualExpression` 으로 옮겨 나눔 | 공급자 계약을 깨지 않음 |
| TE-31 | 소유자 붙이기(9.3)·소유자 바꾸기(TE-22)는 `Oranda.Language/Lexing/OwnerRewriter`. `CreateTableDdl.Reowned`(문자열 바꾸기)는 건드리지 않음 | Core 는 렉서를 못 씀. 데이터 복사 동작을 바꾸지 않음 |
| TE-32 | `CreateTableDdl` 의 `TypeText`·`ColumnDefinition` 은 Core 안 공용 `Schema/TableEdit/ColumnDdl` 로 옮기지 않고, `TableEdit` 쪽에서 `TypeText` 만 호출(Core 안이라 internal 로 됨). 새 테이블의 길이 기준은 TE-6 대로 쓰지 않으므로 `TypeText` 의 `CHAR` 고정 표기 대신 자체 `ColumnTypeText` 를 둔다 | `CreateTableDdl` 은 복사용으로 늘 `n CHAR` 를 씀 — 새 열 규칙(TE-6)과 다름. 복사 동작 보존 |
| TE-33 | 검사·미리보기 읽기 경로 = `ITableEditChecks`(Oracle·Tibero 공급자, 메타데이터 공급자가 쓰는 세션 읽기 경로 재사용) | 설계 5장, D-041 교착 회피 |
| TE-34 | 운영 확인 한 번 = 새 `IExecutionInteraction.ConfirmBatchAsync(statements, PolicyDecision, IReadOnlyList<CheckFinding>, ct)` 기본 구현(기존 `ConfirmChangeAsync` 를 첫 문장으로 부르고 검사 줄을 `ConfirmExtras` 에 실음). 받으면 그 문장 전부를 `PreConfirmed` 로 | 설계 10장 [구현], 확인 창 하나 |
| TE-35 | `HistorySource` 에 `TableEditor`·`ChangeBundle` 을 끝에 더함(저장 값은 이름 문자열인지 확인 후, 숫자면 끝에 더해 기존 값 보존) | 설계 9.3 |

## Review Focus

1. **거짓 차이**: `NUMBER` vs `NUMBER(*,0)`, `CHAR_USED`, 기본값 끝 줄바꿈, 서버가 대문자·따옴표로 돌려주는 가상 식 → 편집 켜고 안 고치면 0문장. T1 `No_edit_produces_no_statements_*`, T3/T4 IT `Untouched_real_tables_produce_no_statements`.
2. **같은 이름 지우고 넣기 / 이름 바꾸고 같은 이름 새로 넣기**: 줄 키로 맞추고 순서 예외(설계 8.3). T1 `Drop_and_readd_same_name_drops_first`, `Rename_then_add_old_name`.
3. **중간 실패 후 남은 문장 다시 계산(TE-5)**: 다시 읽은 원본 위에서 남은 편집만 문장이 되는지. T7 `Partial_failure_recomputes_remaining`.
4. **자동 넣기 겹침**: 묶음 실행·열 순서 바꾸기·데이터 복사 출처는 안 들어가고, 같은 해시는 실행 표시만. T5 `Bundle_run_source_is_not_added`, `Same_hash_marks_run_only`.
5. **소유자 바꾸기**: 문자열 리터럴·주석 안 `SAMPLE.` 은 그대로. T5 `Owner_rewrite_skips_literals_and_comments`.
6. **운영 확인 한 번**: 운영에서 3문장 실행 시 확인 창 1번, 문장 종류는 Ddl 그대로(이력·일지 문장별). T10 `Production_multi_statement_confirms_once`.

---

## 레인 분해표 (coordinator 입력)

| id | 제목 | 크기 | 레인(PC) | 선행 | 무거운 명령(시험 범위) |
|---|---|---|---|---|---|
| T0 | Tibero 실측 1차(설계 12장 10개)·2차(파티션·저장·LOB·이름·신원·인덱스·GTT/IOT·휴지통) | M | te-w30-probe (win30) | - | Tibero IT(win30) |
| T1 | Core 계약·편집 모델·차이·문장·순서·정규화 + 메타데이터 init 멤버(TE-29) | L | te-core (win144) | - | 아니오(Core.Tests TableEdit) |
| T2 | 이름 규칙(`PK_`… 길이 한도)·의뢰 스크립트 형식(9.4) | S | te-m1 (Mac, 16:30까지) → 못 끝내면 te-core | - | 아니오(Core.Tests) |
| T3 | Oracle 방언·메타데이터 보강·검사 읽기(`ITableEditChecks`)·Oracle IT | L | te-oracle (win144) | T1 계약 커밋 | Oracle IT(win144 컨테이너) |
| T4 | Tibero 방언·메타데이터 보강·검사 읽기·Tibero IT | L | te-w30-dialect (win30) | T0 1차, T1 계약 | Tibero IT(win30) |
| T5 | 반영 묶음 저장(0006)·자동 넣기·소유자 붙이기/바꾸기(Language)·`HistorySource`·`FeatureCatalog` | L | te-bundle (win144) | T2(스크립트 형식) | 아니오(Platform·Language·Core Tests) |
| T6 | 객체 상세 편집 모드(열·제약·인덱스·설명), 편집 표 컨트롤, DDL 미리보기·아래 단추, 버림 확인 | L | te-ui (win144) | T1 | 아니오(App.Tests TableEdit) |
| T7 | 실행 연결: 검사 창·운영 확인 한 번(TE-34)·`PreConfirmed` 문장별 실행·성공 후 다시 읽기·중간 실패 재계산·가상 식 미리보기 | M | te-ui | T3, T6 | App.Tests + Oracle IT 일부 |
| T8 | 새 테이블 탭·「이 구조로 새 테이블…」·스키마 브라우저 메뉴·활성 조건 키 | M | te-ui2 (win144) | T6 | 아니오(App.Tests) |
| T9 | 반영 묶음 창(Order 46)·실행… 창·의뢰용 스크립트 저장 창(소유자 바꾸기) | L | te-bundle | T5 | 아니오(App.Tests ChangeBundle) |
| T10 | 묶음 실행 검사(문장 해석 범위)·운영 한 번 확인 | S | te-bundle | T7, T9 | App.Tests |
| T11 | 매뉴얼(`site/…/schema/table-editor.md` 새 쪽 + `tools/db-tools.md` 반영 묶음 절)·`docs/idea.md` 1장 | S | te-docs (win144, sonnet) | T7, T9 | 아니오 |
| T12 | 매뉴얼 그림(`ManualTableEditScreens`: 편집 열 페이지·새 테이블·묶음 창·검사 창, Windows 렌더) | S | te-w30-manual (win30) | T8, T9 | App.Tests Manual(win30) |
| **2차** | | | | | |
| T13 | 2차 Core: 테이블 이름 바꾸기·저장 속성·LOB 저장 절·신원/숨김/도메인 열·함수 기반/비트맵 인덱스 차이·문장 | L | te-core | T1, T0 2차 | Core.Tests |
| T14 | 2차 Core+방언: 파티션 모델·차이·문장(RANGE/LIST/HASH/INTERVAL, ADD/DROP/SPLIT/MERGE/RENAME/TRUNCATE) + Oracle·Tibero 카탈로그 읽기 | L | te-oracle(Oracle) + te-w30-dialect(Tibero) | T13 계약, T0 2차 | Oracle IT·Tibero IT |
| T15 | 2차 화면: 「파티션」「저장」 페이지, 열 페이지 신원·숨김·도메인 칸, 인덱스 종류 칸, 테이블 이름 바꾸기, Tibero 미지원 흐리게+이유 | L | te-ui | T13, T14, T7 | App.Tests |
| T16 | 2차: 새 테이블 작업 저장·이어 하기(TE-10 뒤집음) — `oranda.db` 표 `table_draft`(0007) | M | te-ui2 | T8 | Platform·App Tests |
| T17 | 2차: 형식 변경 자동 데이터 변환(새 열 → UPDATE 복사 → 옛 열 DROP → RENAME, 문장 4개, ⚠)·GTT/IOT 생성 | M | te-core(Core)+te-ui2(화면) | T13, T7 | Core·App Tests + Oracle IT |
| T18 | 전체 통합 시험(Oracle IT 26ai·11g, Tibero IT, 전체 App.Tests 1번)·세 브랜치 ff·answer-2 | S | 조정자 | 전부 | 전체(win144 + win30 대행) |

동시 레인 5개 상한: 처음 = te-w30-probe, te-core, te-m1, te-bundle(T2 기다리는 동안 저장소·자동 넣기부터), te-oracle(T1 계약 뒤). te-ui 는 T1 계약 커밋 뒤 te-m1 자리에 띄운다.

---

### Task 0: Tibero 실측 (te-w30-probe, win30)

**Files:** Create `tests/Oranda.Tibero.Tests/Integration/TableEditProbeIt.cs`, `docs/design/tibero-table-edit-probe.md`.

- [ ] 1차 10개(설계 12장) 시험을 쓰고 돌려 표로 적는다. 1차가 끝나면 진행 보고(조정자가 설계 12장에 옮기고 T4 를 띄운다).
- [ ] 2차 8개(파티션·저장 속성·LOB·이름 바꾸기·신원/숨김/도메인·함수 기반/비트맵·GTT/IOT·휴지통) 실측.
- [ ] `ORT_TE_` 객체 정리 확인, bundle 로 머지 요청.

### Task 1: Core 계약·편집 모델·차이·문장 (te-core)

**Files:**
- Create `src/Oranda.Core/Schema/TableEdit/`: `TableDraft.cs`(`TableDraft`, `ColumnDraft`, `ConstraintDraft`, `IndexDraft`, `IndexColumnDraft`, `CharSemantics`, `DeleteRule`), `TableDraftFactory.cs`(`FromTable(TableInfo, ITableEditDialect)`, `Empty(owner)`, `CopyStructure(TableInfo, owner, name, dialect)`), `TableDraftNormalizer.cs`, `TableDraftDiff.cs`, `TableEditScriptBuilder.cs`(→ `TableEditScript { IReadOnlyList<TableEditStatement> Statements }`, `TableEditStatement(SqlStatement Statement, string RowKey, TableEditPage Page, TableEditFlags Flags)`), `ITableEditDialect.cs`, `ITableEditChecks.cs`(계약만: `CountRowsAsync`, `OverflowCountAsync`, `NullCountAsync`, `DuplicateKeyCountAsync`, `OrphanCountAsync`, `CheckViolationCountAsync`, `PreviewExpressionAsync`, `ValidateExpressionAsync`, `NameExistsAsync`, `DependentsAsync`, `NumRowsAsync`), `CheckFinding.cs`(`CheckSeverity {Ok, Warning, Block}`), `TableEditCheckPlanner.cs`(편집 → 검사 항목 목록, 판정 규칙 설계 10장 표).
- Modify `src/Oranda.Core/Metadata/IMetadataProvider.cs`(TE-29 init 멤버), `CachedMetadataProvider.cs`(`PayloadVersion` 3).
- Test `tests/Oranda.Core.Tests/Schema/TableEdit/*Tests.cs`(가짜 방언 `FakeTableEditDialect`).

`ITableEditDialect` 멤버(초안, T3·T4 가 구현):
```csharp
public interface ITableEditDialect
{
    Dbms Dbms { get; }
    IReadOnlyList<string> TypeNames { get; }            // 형식 목록(설계 7.2)
    int MaxIdentifierBytes(ISessionInfo? server);        // 30 또는 128
    bool SupportsVirtualColumnModify { get; }            // 설계 8.2 「가상 식 변경」, 아니면 DROP+ADD ⚠
    bool PurgesOnDrop { get; }                           // Tibero 휴지통 꺼짐 → DROP ⚠ 문구
    TableEditCapabilities Capabilities { get; }          // 2차 기능별 지원 여부 + 미지원 이유 문자열 키
    string NormalizeType(ColumnDraft c);                 // 거짓 차이 정규화(설계 8.4)
    string? NormalizeDefault(string? serverText);
    string? NormalizeExpression(string? serverText);
    string? NormalizeCondition(string? serverText);
}
```
문장 꼴(설계 8.2)은 Core 빌더가 만들고, 방언이 다른 곳만 방언 멤버로 뺀다(T0 결과로 T3·T4 가 더함).

- [ ] 시험 먼저: 설계 8.2 표 모든 줄, 순서 8.3(같은 이름 지우고 넣기 예외 포함), 고쳤다 되돌리기 = 0, 정규화 8.4 각 항목, 한 열 여러 속성 = MODIFY 1개(TE-11), 새 테이블 = CREATE 1개 + FK 뒤 ALTER + COMMENT + CREATE INDEX(설계 7.5), 이 구조로 = 이름 다시 만들기·신원 속성 빼기, 서버 이름 제약 ⚠(TE-8), 지운 열을 쓰는 제약·인덱스 먼저 지움, 검사 계획(설계 10장 ✖/⚠ 각 조건).
- [ ] 구현 → 통과 → 계약 파일만 먼저 체크포인트 머지 요청(다른 레인 선행), 나머지 머지 요청.

### Task 2: 이름 규칙·의뢰 스크립트 형식 (te-m1, Mac)

**Files:** Create `src/Oranda.Core/Schema/TableEdit/ConstraintNaming.cs`(`Next(kind, table, existingNames, maxBytes) → string`, `PK_<T>`·`UK_<T>_NN`·`FK_<T>_NN`·`CK_<T>_NN`·`IX_<T>_NN`, 가장 큰 번호 + 1 두 자리, 바이트 한도 넘으면 테이블 이름 쪽을 UTF-8 글자 경계에서 줄임), `RequestScriptWriter.cs`(`Write(RequestScriptHeader header, IReadOnlyList<SqlStatement> statements, string newline) → string`: 머리 주석(이름·날짜·DBMS·문장 수·테이블스페이스 안내 한 줄, TE-22), DDL `;`, PL/SQL 줄 `/`, DDL 이 아닌 문장이 있으면 `InvalidOperationException`; 파일 쓰기는 UTF-8 BOM 없음 `RequestScriptWriter.Encoding`). Test `tests/Oranda.Core.Tests/Schema/TableEdit/ConstraintNamingTests.cs`, `RequestScriptWriterTests.cs`.

- [ ] 시험 먼저(번호 증가·두 자리·SYS_ 이름 무시·30/128 바이트·한글 줄이기·머리 주석에 접속 이름 없음·`/` 줄·DML 섞이면 거부) → 구현 → bundle 머지 요청. 16:20 까지 안 되면 진행 상태로 bundle 만들고 보고(te-core 가 이어받음).

### Task 3: Oracle 방언·검사 읽기·IT (te-oracle)

**Files:** Create `src/Oranda.Oracle/Schema/OracleTableEditDialect.cs`, `OracleTableEditChecks.cs`; Modify Oracle 메타데이터 카탈로그 SQL(`meta.columns`·constraints·indexes, `.11.sql` 변형 포함)로 TE-29 값 읽기; Test `tests/Oranda.Oracle.Tests/Schema/OracleTableEditDialectTests.cs`, `tests/Oranda.Oracle.IntegrationTests/Schema/TableEditIntegrationTests.cs`.

- [ ] 가상 식 `MODIFY (c AS (expr))` 허용 여부를 26ai·11g 에서 실측해 `SupportsVirtualColumnModify` 를 정하고 설계 8.2 에 결과 적는 안을 보고.
- [ ] IT: 실측 테이블 여러 개(가상·CHAR 기준·NUMBER(*,0)·기본값 줄바꿈·FK·DESC 인덱스) 「안 고치면 0문장」, 8.2 표 문장 각각 실행 성공, 검사 질의 ✖ 각 경우 데이터.
- [ ] 26ai·11g(`podman start oranda-ora11g`) 둘 다 `--filter "FullyQualifiedName~TableEdit"`.

### Task 4: Tibero 방언·검사 읽기·IT (te-w30-dialect, win30)

T3 와 같은 꼴을 `src/Oranda.Tibero/Schema/TiberoTableEditDialect.cs`·`TiberoTableEditChecks.cs`·Tibero 카탈로그 SQL·`tests/Oranda.Tibero.Tests/Integration/TiberoTableEditIt.cs` 로. T0 표를 입력으로 쓰고, 미지원 항목은 `Capabilities` 에 이유와 함께 끈다.

### Task 5: 반영 묶음 저장·자동 넣기·소유자 (te-bundle)

**Files:**
- Create `src/Oranda.Platform/Migrations/OrandaDb/0006_change_bundle.sql`(설계 9.1 표 3개), `src/Oranda.Core/ChangeBundle/`(`ChangeBundle`, `ChangeBundleItem`, `ChangeBundleRun`, `IChangeBundleStore`, `ChangeBundleRules.cs` = 자동 넣기 조건(9.3) 순수 판정, `StatementHash`), `src/Oranda.Platform/ChangeBundle/SqliteChangeBundleStore.cs`(`SqliteChangeJournalStore` 꼴), `ChangeBundleRecorder.cs`(`IStartupTask`, `StatementExecuted` 구독), `src/Oranda.Language/Lexing/OwnerRewriter.cs`(`Qualify(text, currentSchema)`·`Rewrite(text, map)`, 리터럴·주석 건드리지 않음, 못 고치면 `Changed=false`).
- Modify `src/Oranda.Core/History/HistoryEntry.cs`(TE-35), `src/Oranda.Core/Features/FeatureCatalog.cs`·`FeatureIds.cs`(「테이블 편집」 Operations/WritesOnly, 「반영 묶음」 DevAssist) + `FeatureCatalogTests` 수.
- Test Platform·Core·Language 각각.

- [ ] 시험 먼저: 마이그레이션 번호·멱등, 「지금 묶음」 하나만, 출처·DBMS·대상 소유자·해시 중복(실행 표시만), 실패 문장 안 넣음, 문장 고치면 실행 표시 지움(TE-14), 소유자 붙이기/바꾸기(리터럴·주석·따옴표 이름).

### Task 6: 객체 상세 편집 모드 (te-ui)

**Files:** Create `src/Oranda.App/Features/TableEdit/`(`TableEditSession.cs` = 탭 단위 편집 상태·원본/편집 모델·미리보기, `TableEditGrid` 편집 표 컨트롤 또는 `DetailTable` 편집 확장 — 레인이 정하고 결정 보고, `DdlPreviewPanel`), Modify `ObjectDetailViewModel.cs`·`ObjectDetailView.axaml(.cs)`(✎ 토글, 페이지 바뀐 건수, 새로 고침·닫기·끄기 버림 확인, 열 순서 바꾸기 앞 확인). Test `tests/Oranda.App.Tests/Features/TableEdit/*`.

- [ ] Headless: 편집 켜기(캐시 무효화 후 새로 읽기 TE-28)·노랑/초록/빨강 표시·페이지 이동 유지·미리보기 = 차이·버림 확인 3경로·GTT 등 특수 테이블은 주석·기본값만.

### Task 7: 실행 연결 (te-ui)

- [ ] `TableEditRunner`: 검사(T1 계획 + `ITableEditChecks`) → 개발 ⚠만 = 창 없음(TE-20), 운영 = `ConfirmBatchAsync` 한 번(TE-34) → `ExecuteScriptAsync(Stop, Source=TableEditor, PreConfirmed=…)` → 성공: 캐시 무효화·다시 열기·편집 끄기 / 중간 실패: 다시 읽고 남은 편집 재계산(TE-5). 가상 식 미리보기(설계 7.6). 스크립트 저장…(T2 형식, 테이블 이름 머리).

### Task 8: 새 테이블 탭 (te-ui2)

- [ ] 「새 테이블…」(소유자 노드·테이블 폴더, 새 활성 조건 키 `ownerNodeSelected`), 「이 구조로 새 테이블…」(테이블 노드), 「✎ 새 테이블」 탭(소유자·이름·주석 칸, 열·제약·인덱스만, 토글 고정), 이름 따라 바뀌기(사용자가 고친 이름 제외), 실행 성공 → 같은 탭이 상세로·스키마 브라우저 캐시 무효화, 닫을 때 버림 확인. 메뉴 수 시험 +1.

### Task 9: 반영 묶음 창 (te-bundle)

- [ ] `ChangeBundleViewModel`(`[OrandaToolPanel("oranda.panel.changeBundle", ToolPanelKind.Tool, DockLocation.Bottom, Order = 46)]`), 메뉴 「도구 > 반영 묶음」, 제목줄(지금 묶음·＋·이름 바꾸기·지우기), 도구줄(↑↓·－·실행…·의뢰용 스크립트 저장…·DBMS), 표(체크·#·요약·출처·개발·테스트·운영), 전문 보기·고치기, 대상 소유자 편집, 「실행…」 창(접속 고르기·소유자 바꾸기), 저장 창(소유자 바꾸기·UTF-8), 넣을 때 제목 건수 잠깐 표시. 편집기 「묶음에 넣기 ▾」 연결.

### Task 10: 묶음 실행 검사 (te-bundle)

- [ ] 묶음 문장 해석(ALTER MODIFY 길이·NOT NULL, ADD CONSTRAINT PK/UK/FK/CHECK, DROP 계열)만 검사, 나머지 그대로. 운영 한 번 확인.

### Task 11: 매뉴얼·idea.md (te-docs)

- [ ] `site/src/content/docs/schema/table-editor.md`(편집 켜기·열·제약·인덱스·새 테이블·검사·실행·스크립트 저장, 2차 페이지), `tools/db-tools.md` 「반영 묶음」 절, `schema/browser.md` 객체 상세 절에서 링크, `docs/idea.md` 1장 「테이블 구조 편집」 첫 항목 → 완료(2차 포함 여부 표시).

### Task 12: 매뉴얼 그림 (te-w30-manual, win30)

- [ ] `tests/Oranda.App.Tests/Manual/ManualTableEditScreens.cs` 로 `table-edit-columns`, `table-edit-new`, `change-bundle`, `table-edit-check` 를 Windows 렌더로 찍어 `site/src/assets/screens` 에 넣고 T11 쪽에 그림 연결.

### Task 13~17: 2차 (설계 18장)

**순서(사용자 지시, TE-46)**: T13(이름 바꾸기·신원/숨김/도메인·함수 기반/비트맵·GTT/IOT) → T16(작업 저장) → T15 앞 몫(그 화면) → 맨 마지막에 T14(파티션)·T13 의 저장 속성·LOB 저장 몫·T17 자동 형식 변환. 이 마지막 묶음의 화면은 사용자 시안 `request-4.md` 가 온 뒤 설계·구현한다(그 전에는 Core·방언 문장 꼴만 해도 되지만, 화면 일은 시작하지 않는다).

각 Task 는 설계 18장의 해당 절을 정본으로 T1·T3·T4·T6 과 같은 규칙(시험 먼저, 0문장 정규화, ✖/⚠, 묶음 연동, Tibero 미지원 흐리게+이유)으로 한다. Tibero 지원 여부는 T0 2차 결과로 정한다.

### Task 18: 마감 (조정자)

- [ ] Oracle IT 26ai·11g 전체(`--filter "Category!=Perf"`), Tibero IT(win144 환경 변수, `ORANDA_IT_TIBERO_WRITE=1`), App.Tests 전체 1번(win30 대행 가능), `dotnet build Oranda.sln -c Debug -warnaserror`.
- [ ] 설계 12장 실측 결과·16장 결정 목록·18장 갱신, wbs/decisions 갱신, 세 브랜치 ff push, answer-2.md(한 일·결정·사용자 확인 목록·남은 일).
